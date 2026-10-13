import { useCallback, useEffect, useLayoutEffect, useRef, useState,
  type KeyboardEvent, type PointerEvent, type WheelEvent } from "react";
import type { NewestHistoryRequest, NewestHistoryResult, OlderHistoryRequest } from "./HistoryPanel";
import { historyMessage } from "./history";
import { translate, type Locale, type MessageKey } from "./localization";

export type TimelineNotice = Readonly<{ key: MessageKey; parameters?: Readonly<Record<string, string>>; error?: string }> | null;
// Only typed UI notices enter this formatter; never translate captured message text.
export function timelineNotice(locale: Locale, notice: TimelineNotice): string {
  return notice ? translate(locale, notice.key, { label: translate(locale, "User or assistant message"), ...notice.parameters,
    ...(notice.error !== undefined ? { error: translate(locale, historyMessage(notice.error)) } : {}) }) : "";
}

export type ScrollMetrics = Readonly<{ scrollTop: number; scrollHeight: number; clientHeight: number }>;

export const timelineFollowThreshold = 72;

// Native inner scrolling consumes this gesture; an outward boundary gesture may chain
// to the timeline and must still establish genuine outer scroll intent.
function codeCanScroll(code: HTMLElement, delta: number): boolean {
  return delta < 0 ? code.scrollTop > 0 : code.scrollTop < code.scrollHeight - code.clientHeight - 1;
}

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

/**
 * A reader who goes up has the turns before the loaded ones read once the first loaded row is within a
 * screen above the view, and never less than this: the rows are there before the reader reaches them.
 */
export const olderHistoryDistance = 600;

export function nearTimelineTop(metrics: Pick<ScrollMetrics, "scrollTop" | "clientHeight">): boolean {
  return metrics.scrollTop <= Math.max(olderHistoryDistance, metrics.clientHeight);
}

/**
 * Whether a scroll that took the view from `previous` to `current` is the reader going up. The browser moves the
 * view up by itself only with a change of layout: rows above that change size, a view that grows at the end of
 * the timeline. So a scroll up is the reader's when the content and the view kept their sizes, or when it
 * follows a move up of the reader (`goingUp`), whose scrolls bring rows into the layout as they go.
 */
export function readerScrolledUp(previous: ScrollMetrics, current: ScrollMetrics, goingUp: boolean): boolean {
  return current.scrollTop < previous.scrollTop
    && (goingUp || current.scrollHeight === previous.scrollHeight && current.clientHeight === previous.clientHeight);
}

export type MessageNavigation = "messagePrevious" | "messageNext" | "messageFirst";
/** `older`: there was no message to go to, and the turns before the loaded ones are being read. */
export type MessageNavigationResult = Readonly<{ status: "moved" | "boundary" | "unavailable"; label?: string; older?: boolean }>;

// App-owned, bounded in-memory preferences. A new mounted session owns its own selection;
// restoring an old position must wait for its journal (or an explicit read error), not the loading skeleton.
export function createTimelineScrollMemory() {
  const positions = new Map<string, { top: number; following: boolean; rewinds: number }>();
  return {
    /**
     * The position of a session's timeline. `rewinds` is the number of times the session was rewound: a position
     * kept before a rewind is one of another conversation, and the timeline starts at its end again.
     */
    open(id: string, rewinds = 0) {
      function remember(id: string, top: number, following: boolean) {
        positions.delete(id);
        positions.set(id, { top, following, rewinds });
        if (positions.size > 64) positions.delete(positions.keys().next().value!);
      }
      const kept = positions.get(id);
      const saved = kept?.rewinds === rewinds ? kept : { top: 0, following: true };
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
export function useTimelinePosition(sessionId: string, memory: ReturnType<typeof createTimelineScrollMemory>, rewinds = 0) {
  const elementRef = useRef<HTMLDivElement>(null);
  const [selection] = useState(() => memory.open(sessionId, rewinds));
  const [following, setFollowing] = useState(selection.following);
  const restoreFrame = useRef(0);
  const observedHeight = useRef<number | null>(null);
  const layoutUntil = useRef(0);
  const scrollIntent = useRef<{ top: number; direction: -1 | 0 | 1; until: number } | null>(null);
  const scrollbarDrag = useRef<number | null>(null);
  const scrollbarDragTop = useRef(0);
  const touchStart = useRef<{ id: number; y: number; code: HTMLElement | null } | null>(null);
  const prependMetrics = useRef<{ metrics: ScrollMetrics; anchor: HTMLElement | null; top: number } | null>(null);
  // The history reads the turns before the loaded ones through this. Only a move up of the reader asks: the
  // wheel, a key, a finger, the scrollbar let go, a scroll up, a message gone to. Until `goingUp`, the scrolls
  // that follow such a move are part of it (the wheel and the keys scroll over a few frames). `lastScroll` is
  // where the view was at the last scroll, or where this hook put it since, and the sizes it had.
  const olderRequestRef = useRef<OlderHistoryRequest | null>(null);
  const goingUp = useRef(0);
  const lastScroll = useRef<ScrollMetrics | null>(null);
  const pressedInside = useRef(false);
  const messageAnchor = useRef<{ row: HTMLElement; top: number } | null>(null);
  const resetMessageNavigation = useCallback(() => { messageAnchor.current = null; }, []);
  const pauseIfUnfollowed = useCallback(() => {
    if (selection.following()) return;
    const element = elementRef.current;
    if (element) selection.pauseAt(element.scrollTop);
    setFollowing(false);
  }, [selection]);
  useEffect(() => {
    // A native scrollbar drag can leave the scroller. Observe its lifetime without
    // capturing the pointer or interfering with the browser's scrollbar handling.
    const move = (event: globalThis.PointerEvent) => {
      if (scrollbarDrag.current !== event.pointerId) return;
      if (event.target instanceof Node && elementRef.current?.contains(event.target)) return;
      if (event.buttons & 1) markScrollIntent(0);
      else endScrollbarDrag(event.pointerId);
    };
    const end = (event: globalThis.PointerEvent) => endScrollbarDrag(event.pointerId);
    const blur = () => { endScrollbarDrag(undefined, false); touchStart.current = null; };
    // After a press in the timeline that focuses nothing in it, the keys scroll it but are not sent to it: they
    // are seen here, so that going up with nothing left to scroll still reads the turns before. The keyboard is
    // then on nothing, or on what holds the timeline: the panel of its tab is focusable and takes such a press.
    const press = (event: globalThis.PointerEvent) => {
      pressedInside.current = event.target instanceof Node && !!elementRef.current?.contains(event.target);
    };
    const key = (event: globalThis.KeyboardEvent) => {
      if (!pressedInside.current || event.defaultPrevented || event.altKey || event.ctrlKey || event.metaKey) return;
      const focused = document.activeElement;
      if (focused && focused !== document.body && !focused.contains(elementRef.current)) return;
      if (["ArrowUp", "PageUp", "Home"].includes(event.key) || event.key === " " && event.shiftKey) moveUp();
    };
    window.addEventListener("pointermove", move);
    window.addEventListener("pointerup", end);
    window.addEventListener("pointercancel", end);
    window.addEventListener("blur", blur);
    window.addEventListener("pointerdown", press, true);
    window.addEventListener("keydown", key);
    return () => {
      window.removeEventListener("pointermove", move);
      window.removeEventListener("pointerup", end);
      window.removeEventListener("pointercancel", end);
      window.removeEventListener("blur", blur);
      window.removeEventListener("pointerdown", press, true);
      window.removeEventListener("keydown", key);
      blur();
    };
  }, []);
  useEffect(() => {
    const element = elementRef.current;
    if (!element) return;
    let frame = 0;
    const scrollBottom = () => {
      if (!selection.following()) return;
      layoutUntil.current = performance.now() + 120; // Native scroll anchoring can settle after the first resize frame.
      cancelAnimationFrame(frame);
      frame = requestAnimationFrame(() => {
        if (selection.following()) element.scrollTop = bottomScrollTop(element);
      });
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
    observedHeight.current = element.scrollHeight;
    restoreFrame.current = requestAnimationFrame(() => selection.finishRestore());
  }
  // Reads the turns before the loaded ones when the view is near their top. Not while the scrollbar is held:
  // rows added under a held thumb would move the view, so its release asks.
  function askOlder(): boolean {
    const element = elementRef.current;
    return !!element && scrollbarDrag.current === null && nearTimelineTop(element) && (olderRequestRef.current?.() ?? false);
  }
  // Where this hook put the view: the scroll that reports it is not a move of the reader.
  function placed(element: HTMLElement) {
    lastScroll.current = { scrollTop: element.scrollTop, scrollHeight: element.scrollHeight, clientHeight: element.clientHeight };
  }
  function scroll(element: HTMLDivElement) {
    const last = lastScroll.current;
    placed(element);
    if (last && readerScrolledUp(last, element, performance.now() <= goingUp.current)) askOlder();
    const intent = scrollIntent.current;
    const moved = !!intent && performance.now() <= intent.until && element.scrollTop !== intent.top &&
      (intent.direction === 0 || (element.scrollTop - intent.top) * intent.direction > 0);
    if (moved) scrollIntent.current = null;
    const movingAway = moved && intent?.direction !== 1;
    const geometryChanged = observedHeight.current !== null && observedHeight.current !== element.scrollHeight;
    const layoutScroll = selection.following() && !movingAway && performance.now() <= layoutUntil.current;
    if (layoutScroll || geometryChanged && (!selection.following() || !movingAway)) {
      // Detail/layout changes can emit a browser scroll before ResizeObserver follows the new bottom.
      // Do not infer a reader's change of follow preference from geometry alone. Explicit
      // movement away wins; moving toward a growing tail must not opt a follower out.
      // An unfollowed reader's preference is never replaced by a simultaneous layout change.
      observedHeight.current = element.scrollHeight;
      if (selection.following()) element.scrollTop = bottomScrollTop(element);
      else selection.pauseAt(element.scrollTop);
      return;
    }
    observedHeight.current = element.scrollHeight;
    setFollowing(selection.scroll(element));
  }
  function markScrollIntent(direction: -1 | 0 | 1) {
    const element = elementRef.current;
    if (element) scrollIntent.current = { top: element.scrollTop, direction, until: performance.now() + 500 };
  }
  // The reader moves up. With nothing to scroll (a latest turn shorter than the view, or the view already at
  // the top) no scroll follows, so the move itself asks for the turns before.
  function moveUp() {
    markScrollIntent(-1);
    goingUp.current = performance.now() + 500;
    askOlder();
  }
  function endScrollbarDrag(id?: number, released = true) {
    if (scrollbarDrag.current === null || id !== undefined && scrollbarDrag.current !== id) return;
    scrollbarDrag.current = null;
    scrollIntent.current = null;
    // The thumb is let go where it was taken up to: the turns before are read now that nothing holds the view.
    const element = elementRef.current;
    if (released && element && element.scrollTop <= scrollbarDragTop.current) askOlder();
  }
  function wheel(event: WheelEvent<HTMLDivElement>) {
    if (event.defaultPrevented || event.ctrlKey || event.deltaY === 0) return;
    const target = event.target as Element;
    const code = target.closest<HTMLElement>("pre.timeline-code");
    if (code ? codeCanScroll(code, event.deltaY) : target.closest(".event-details pre")) return;
    if (event.deltaY > 0) markScrollIntent(1);
    else moveUp();
  }
  function keyDown(event: KeyboardEvent<HTMLDivElement>) {
    if (event.defaultPrevented || event.altKey || event.ctrlKey || event.metaKey ||
      (event.target as Element).closest("input, select, textarea, [contenteditable], .event-details pre")) return;
    if (event.key === " " && (event.target as Element).closest("button, summary, a")) return;
    if (["ArrowUp", "PageUp", "Home"].includes(event.key) || event.key === " " && event.shiftKey) moveUp();
    else if (["ArrowDown", "PageDown", "End"].includes(event.key) || event.key === " " && !event.shiftKey) markScrollIntent(1);
  }
  function pointerDown(event: PointerEvent<HTMLDivElement>) {
    if (event.pointerType === "touch") {
      const target = event.target as Element;
      const code = target.closest<HTMLElement>("pre.timeline-code");
      touchStart.current = !code && target.closest(".event-details pre") ? null : { id: event.pointerId, y: event.clientY, code };
      return;
    }
    const element = event.currentTarget;
    if (event.target === element && event.buttons & 1 &&
      event.clientX >= element.getBoundingClientRect().left + element.clientWidth) {
      scrollbarDrag.current = event.pointerId;
      scrollbarDragTop.current = element.scrollTop;
      markScrollIntent(0); // Only the scrollbar gutter, not a detail or Wrap control.
    }
  }
  function pointerMove(event: PointerEvent<HTMLDivElement>) {
    if (scrollbarDrag.current === event.pointerId) {
      if (event.buttons & 1) markScrollIntent(0);
      else endScrollbarDrag(event.pointerId);
    }
    const start = touchStart.current;
    if (event.pointerType === "touch" && start?.id === event.pointerId && Math.abs(event.clientY - start.y) > 3) {
      const delta = start.y - event.clientY;
      if (!start.code || !codeCanScroll(start.code, delta)) { if (delta > 0) markScrollIntent(1); else moveUp(); }
      if (start.code) start.y = event.clientY;
    }
  }
  function pointerEnd(event: PointerEvent<HTMLDivElement>) {
    if (touchStart.current?.id === event.pointerId) touchStart.current = null;
    endScrollbarDrag(event.pointerId);
  }
  function beforeOlderPage() {
    const element = elementRef.current;
    if (!element) return;
    const viewport = element.getBoundingClientRect();
    const anchor = Array.from(element.querySelectorAll<HTMLElement>(".timeline-message"))
      .find(row => row.getBoundingClientRect().bottom > viewport.top) ?? null;
    prependMetrics.current = { metrics: { scrollTop: element.scrollTop, scrollHeight: element.scrollHeight, clientHeight: element.clientHeight },
      anchor, top: anchor?.getBoundingClientRect().top ?? 0 };
    // A reader at the end of the timeline stays there and goes on following: the older rows arrive above.
    if (selection.following()) return;
    selection.pauseAt(element.scrollTop);
    setFollowing(false);
  }
  function afterOlderPage() {
    const element = elementRef.current;
    const previous = prependMetrics.current;
    prependMetrics.current = null;
    if (!element || !previous) return;
    const top = previous.anchor?.isConnected && element.contains(previous.anchor)
      ? element.scrollTop + previous.anchor.getBoundingClientRect().top - previous.top
      : preservePrependScrollTop(previous.metrics, element.scrollHeight);
    // Away from its top the browser has already kept the view on its rows. Placing it again would end the
    // scroll of a wheel or of a key that is still under way, short of where the reader was going.
    if (Math.abs(top - element.scrollTop) >= 0.5) element.scrollTop = top;
    placed(element);
    if (!selection.following()) selection.pauseAt(element.scrollTop);
  }
  function jump() {
    const element = elementRef.current;
    if (element) {
      scrollIntent.current = null;
      scrollbarDrag.current = null;
      touchStart.current = null;
      layoutUntil.current = 0;
      selection.jump(element);
      element.scrollTop = bottomScrollTop(element);
      observedHeight.current = element.scrollHeight;
    }
    setFollowing(true);
  }
  function pause() {
    const element = elementRef.current;
    if (element) selection.pauseAt(element.scrollTop);
    setFollowing(false);
  }
  // Shows the message of a journal offset at the top of the timeline, when the window holds it. The reader
  // is then where they chose to be, not at the end. With `mark`, the row is pointed out for a moment.
  function showRow(key: string, mark = false): boolean {
    const element = elementRef.current;
    const row = /^\d+$/u.test(key) ? element?.querySelector<HTMLElement>(`[data-row-offset="${key}"], [data-prompt-offset="${key}"]`) : null;
    if (!element || !row) return false;
    element.scrollTop += row.getBoundingClientRect().top - element.getBoundingClientRect().top - 8;
    placed(element);
    messageAnchor.current = null;
    selection.pauseAt(element.scrollTop);
    setFollowing(false);
    if (mark) {
      // Asked for twice, the mark is drawn again from its start.
      row.removeAttribute("data-revealed");
      void row.offsetWidth;
      row.setAttribute("data-revealed", "true");
      row.addEventListener("animationend", () => row.removeAttribute("data-revealed"), { once: true });
    }
    return true;
  }
  // Where the timeline is, and what puts it back there: a window that moved it to show a row gives it back as it was.
  function position(): { top: number; following: boolean } | null {
    const element = elementRef.current;
    return element ? { top: element.scrollTop, following: selection.following() } : null;
  }
  function restorePosition(saved: { top: number; following: boolean } | null) {
    const element = elementRef.current;
    if (!element || !saved) return;
    if (saved.following) { jump(); return; }
    element.scrollTop = Math.min(saved.top, bottomScrollTop(element));
    placed(element);
    selection.pauseAt(element.scrollTop);
    setFollowing(false);
  }
  function messageReady() {
    const element = elementRef.current;
    return !!element?.querySelector('.history[data-window-ready="true"] [data-persisted-message="true"]');
  }
  function navigateMessage(action: MessageNavigation): MessageNavigationResult {
    const element = elementRef.current;
    if (!element || !messageReady()) return { status: "unavailable" };
    const rows = Array.from(element.querySelectorAll<HTMLElement>(
      '.history[data-window-ready="true"] [data-persisted-message="true"]'));
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
      // No message before the first loaded one: the turns before it are read, and the next press goes there.
      return { status: "boundary", older: action === "messagePrevious" && askOlder() };
    }
    const row = rows[index];
    element.scrollTop += row.getBoundingClientRect().top - viewport.top;
    placed(element);
    messageAnchor.current = { row, top: element.scrollTop };
    selection.pauseAt(element.scrollTop);
    setFollowing(false);
    // The first loaded message is the top of what is loaded: going there reads the turns before it.
    if (action === "messageFirst") askOlder();
    return { status: "moved", label: row.querySelector(".message-body")?.textContent?.trim().slice(0, 120) };
  }
  return { elementRef, following, settled, scroll, wheel, keyDown, pointerDown, pointerMove, pointerEnd,
    jump, pause, beforeOlderPage, afterOlderPage, olderRequestRef,
    messageReady, navigateMessage, resetMessageNavigation, pauseIfUnfollowed, showRow, position, restorePosition };
}

// Only a user-requested, generation-matched settled tail can opt a reader into follow.
// Pending intent belongs to the mounted selection, never to a global history request.
// With `tailLoaded` the window already reaches the journal end: going to the newest message is then an
// immediate jump, and the refresh behind it only appends what is new, without announcing itself.
export function useExplicitNewestHistory(sessionId: string, projectId: string | null, epoch: string | null,
  position: ReturnType<typeof useTimelinePosition>, onNotice: (message: TimelineNotice) => void, tailLoaded = false) {
  const requestRef = useRef<NewestHistoryRequest | null>(null);
  const pending = useRef<{ generation: number; sessionId: string; projectId: string | null; epoch: string | null;
    wasFollowing: boolean; anchor: HTMLElement | null; anchorTop: number; quiet: boolean } | null>(null);
  useLayoutEffect(() => {
    if (pending.current && (pending.current.sessionId !== sessionId || pending.current.projectId !== projectId ||
      pending.current.epoch !== epoch)) {
      pending.current = null;
      onNotice(null);
    }
  }, [sessionId, projectId, epoch, onNotice]);
  function cancel() {
    if (!pending.current) return;
    const quiet = pending.current.quiet;
    pending.current = null;
    if (!quiet) onNotice({ key: "Newest history is still loading; automatic follow canceled by newer user navigation." });
  }
  const onTarget = useCallback((generation: number): boolean => {
    if (pending.current?.generation === generation) return true;
    pending.current = null;
    return false;
  }, []);
  function latest() {
    const quiet = tailLoaded;
    const wasFollowing = position.following;
    if (quiet) position.jump();
    const generation = requestRef.current?.();
    if (generation === undefined) return;
    const element = position.elementRef.current;
    const anchor = element ? Array.from(element.querySelectorAll<HTMLElement>(".timeline-message"))
      .find(row => row.getBoundingClientRect().bottom > element.getBoundingClientRect().top) ?? null : null;
    pending.current = { generation, sessionId, projectId, epoch, wasFollowing,
      anchor, anchorTop: anchor?.getBoundingClientRect().top ?? 0, quiet };
    onNotice(quiet ? null : { key: "Refreshing the newest persisted history window…" });
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
      onNotice({ key: "Newest history refresh failed: {error} Follow preference unchanged.", error: result.error });
    }
    else {
      position.jump();
      onNotice(intent.quiet ? null : { key: "Newest persisted history window loaded; following visible content." });
    }
  }
  return { requestRef, available: () => requestRef.current !== null, pending: () => pending.current !== null,
    latest, cancel, onScroll, onTarget, onResult };
}
