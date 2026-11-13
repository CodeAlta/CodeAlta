// What the session that shows the card of a work item runs with, read from the host: the cards take it as their defaults.
import { sessionOperations } from "#neoastra";
import type { ShowingSession } from "./WorkRunsWith";

/** Reads what a session runs with: its model, and its tier or Adaptive (`adaptive:<level>`); null when it cannot be read. */
export async function readShowingSession(epoch: string, sessionId: string, signal: AbortSignal): Promise<ShowingSession | null> {
  const reply = await sessionOperations.choices({ expectedEpoch: epoch, sessionId }, { signal, timeoutMilliseconds: 15000 });
  if (reply.status !== "ok" || reply.epoch !== epoch || !reply.current) return null;
  const { providerKey, modelId, reasoningEffort } = reply.current;
  return { runsWith: { providerId: providerKey, modelId, reasoningEffort },
    tier: reply.adaptive ? reply.adaptiveLevel ? `adaptive:${reply.adaptiveLevel}` : "adaptive" : reply.tier ?? null };
}
