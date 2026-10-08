import { useSyncExternalStore } from "react";
import { AppIcon } from "./AppIcon";
import type { createDraftIndicators } from "./promptDraft";

export type DraftIndicators = ReturnType<typeof createDraftIndicators>;

/**
 * Whether a session shows the mark of an edited prompt. Each keystroke changes the indicators: the component
 * that asks is rendered again only when its own answer changes, and nothing above it is.
 */
export function useDraftIndicator(indicators: DraftIndicators | undefined, sessionId: string | null, selectedId: string | null): boolean {
  const shown = () => !!indicators && sessionId !== null && indicators.visible(sessionId, selectedId);
  return useSyncExternalStore(indicators?.subscribe ?? noSubscription, shown, shown);
}
const noSubscription = () => () => { };

export function SessionDraftBadge({ active }: { active: boolean }) {
  return active ? <span className="session-draft-badge"><AppIcon name="prompt" size={12} />Edited draft</span> : null;
}

/** The badge of a session of the list, which follows the edits of its prompt by itself. */
export function SessionDraftStatus({ indicators, sessionId, selectedId }: { indicators: DraftIndicators; sessionId: string; selectedId: string | null }) {
  return <SessionDraftBadge active={useDraftIndicator(indicators, sessionId, selectedId)} />;
}
