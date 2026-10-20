// The reviews hub of the page, as React reads it: the hub through a context, and one review or the counts of a
// project kept read while a component shows them.
import { createContext, useCallback, useContext, useEffect, useMemo, useSyncExternalStore } from "react";
import { checkoutCounts, type ReviewList, type ReviewProjectCounts, type ReviewScope, type ReviewsHub, type ReviewTarget } from "./reviewsHub";
import type { ReviewCheckoutCounts } from "#neoastra";
import type { ReviewThreadActions } from "./ReviewThreadCard";

/** The reviews hub of the page; null where the window has no host that keeps reviews (a demo, a test without one). */
export const ReviewsHubContext = createContext<ReviewsHub | null>(null);

export const useReviewsHub = () => useContext(ReviewsHubContext);

/**
 * Opens a review where it is done: a file of the project folder in the code editor (a Markdown file rendered), the
 * changes or a file of a worktree in the Changes tab. Null where the window cannot open them.
 */
export const ReviewOpenerContext = createContext<((scope: ReviewScope, target: ReviewTarget) => void) | null>(null);

/** Opens every review in flight, in Work items, on the reviews of a project. Null where the window cannot open them. */
export const ReviewsListOpenerContext = createContext<((projectId: string | null) => void) | null>(null);

const noList: ReviewList = Object.freeze({ loaded: false, status: "unavailable", threads: [], rounds: [], viewed: [], request: null, readOnly: null });
const noCounts: ReviewProjectCounts = Object.freeze({ loaded: false, checkouts: [] });
const noSubscription = () => () => { };

/** The review of a target of a checkout, kept read while the component shows it; null scope or target reads nothing. */
export function useReviewList(hub: ReviewsHub | null, scope: ReviewScope | null, target: ReviewTarget | null): ReviewList {
  const projectId = scope?.projectId ?? null, checkout = scope?.checkout ?? null;
  const kind = target?.kind ?? null, path = target?.kind === "file" ? target.path : null;
  useEffect(() => {
    if (!hub || projectId === null || kind === null) return;
    return hub.retainList({ projectId, checkout }, kind === "file" ? { kind, path: path! } : { kind });
  }, [hub, projectId, checkout, kind, path]);
  const read = useCallback(() => !hub || projectId === null || kind === null ? noList
    : hub.list({ projectId, checkout }, kind === "file" ? { kind, path: path! } : { kind }), [hub, projectId, checkout, kind, path]);
  return useSyncExternalStore(hub?.subscribe ?? noSubscription, read, read);
}

/** The counts of the checkouts of a project, kept read while the component shows them. */
export function useReviewCounts(hub: ReviewsHub | null, projectId: string | null): ReviewProjectCounts {
  useEffect(() => hub && projectId ? hub.retainCounts(projectId) : undefined, [hub, projectId]);
  const read = useCallback(() => hub && projectId ? hub.counts(projectId) : noCounts, [hub, projectId]);
  return useSyncExternalStore(hub?.subscribe ?? noSubscription, read, read);
}

/** The counts of one checkout of a project: the drafts, the open and the addressed comments waiting there. */
export function useCheckoutCounts(hub: ReviewsHub | null, scope: ReviewScope | null): ReviewCheckoutCounts | null {
  const counts = useReviewCounts(hub, scope?.projectId ?? null);
  return scope && counts.loaded ? checkoutCounts(counts, scope.checkout) : null;
}

/** What the cards of a review do, bound to its checkout; null without a hub or a checkout, or while `readOnly`. */
export function useReviewActions(hub: ReviewsHub | null, scope: ReviewScope | null, readOnly = false): ReviewThreadActions | null {
  const projectId = scope?.projectId ?? null, checkout = scope?.checkout ?? null;
  return useMemo(() => {
    if (!hub || projectId === null || readOnly) return null;
    const at = { projectId, checkout };
    return {
      update: async (id, body, suggestion) => (await hub.update(at, id, body, suggestion)).ok,
      remove: async id => (await hub.remove(at, id)).ok,
      resolve: async (id, resolved) => (await hub.resolve(at, id, resolved)).ok,
      reply: async (id, body) => (await hub.reply(at, id, body)).ok,
    };
  }, [hub, projectId, checkout, readOnly]);
}
