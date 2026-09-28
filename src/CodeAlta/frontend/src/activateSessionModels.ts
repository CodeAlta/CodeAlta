import type { ModelCatalogModelsResponse, SessionChoicesResponse } from "#neoastra";

// Explicit user action only. The observed choices endpoint remains passive on navigation.
// Re-read choices after the real catalog service loads; catalog display DTOs are not
// interchangeable with the session-scoped choices used to validate Send.
export async function activateSessionModels(epoch: string, sessionId: string,
  read: () => Promise<SessionChoicesResponse>, load: (provider: string) => Promise<ModelCatalogModelsResponse>,
  current: () => boolean): Promise<SessionChoicesResponse> {
  const before = await read();
  if (!current()) throw new DOMException("Selection changed", "AbortError");
  if (before.status !== "ok" || before.epoch !== epoch || before.sessionId !== sessionId || !before.current)
    throw new Error("Session choices unavailable");
  const provider = before.current.providerKey;
  const catalog = await load(provider);
  if (!current()) throw new DOMException("Selection changed", "AbortError");
  if (catalog.status !== "ok" || catalog.epoch !== epoch || catalog.providerId !== provider || catalog.availability !== "Ready")
    throw new Error("Provider catalog unavailable");
  const after = await read();
  if (!current()) throw new DOMException("Selection changed", "AbortError");
  if (after.status !== "ok" || after.epoch !== epoch || after.sessionId !== sessionId || after.current?.providerKey !== provider)
    throw new Error("Session provider changed");
  return after;
}
