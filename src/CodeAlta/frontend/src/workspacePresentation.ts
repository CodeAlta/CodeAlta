import type { DisplayState } from "./sessionDisplay";
import type { AskPage } from "./sessionAsks";

export function showLiveDisplay(state: DisplayState | null): boolean {
  if (!state) return false;
  if (state.code || state.cleanupBlocked || state.kind === "closed" || state.kind === "error") return true;
  const snapshot = state.snapshot;
  if (!snapshot) return false;
  const session = snapshot.session;
  return snapshot.hasGap || snapshot.evictedSessions !== "0" || snapshot.omittedSessionEvents !== "0"
    || !!session && (session.text.length > 0 || session.toolActivities.length > 0
      || !!session.lifecycle || !!session.statusKind || !!session.statusMessage || (session.queuedPromptCount ?? 0) > 0
      || session.metadataTruncated || session.transportTruncated
      || session.evictedTextItems !== "0" || session.evictedToolActivities !== "0" || session.unsupportedEvents !== "0");
}

export function showAskDetails(page: AskPage | undefined, retainedCount: number, readFailed: boolean, invalidEpoch: boolean): boolean {
  return !!page?.head || !!page?.latest || !!page?.hasMore || retainedCount > 0 || readFailed || invalidEpoch;
}

export function showContextAction(observationPermits: boolean, retained: boolean): boolean {
  return observationPermits || retained;
}

export function promptEditorHeight(contentHeight: number, viewportHeight: number): number {
  return Math.max(50, Math.min(contentHeight, viewportHeight * 0.3, 240));
}
