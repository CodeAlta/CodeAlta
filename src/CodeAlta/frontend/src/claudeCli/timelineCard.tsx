import { createContext, useContext, type ComponentType } from "react";

/** The card that proposes the download of the Claude Code CLI, given to the timeline by the window. */
export type ClaudeCliCard = ComponentType<{ epoch: string; providerKey: string }>;

/**
 * What the timeline of a session needs for the card: the epoch of the host, the provider whose card the composer of
 * the session already shows, and the card itself. Null where nothing can be downloaded (a demo, a fixture). The
 * timeline does not import the card: it reaches the host, which a page without one does not bundle.
 */
export const ClaudeCliEpochContext = createContext<Readonly<{ epoch: string; composerProvider: string | null; Card: ClaudeCliCard }> | null>(null);

/** Whether the error of a provider shows the card in the timeline: not when the composer of the session shows it. */
export function showsTimelineCard(host: Readonly<{ composerProvider: string | null }>, providerKey: string): boolean {
  return host.composerProvider?.toLowerCase() !== providerKey.toLowerCase();
}

/** The card of an error whose provider has no Claude Code CLI to run, in the timeline of its session. */
export function TimelineClaudeCli({ providerKey }: { providerKey: string }) {
  const host = useContext(ClaudeCliEpochContext);
  return host && showsTimelineCard(host, providerKey) ? <host.Card epoch={host.epoch} providerKey={providerKey} /> : null;
}
