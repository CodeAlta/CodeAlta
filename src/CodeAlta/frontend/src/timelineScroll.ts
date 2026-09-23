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
