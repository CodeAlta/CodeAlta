import type { ModelCatalogModelsResponse, SessionChoicesResponse } from "#neoastra";

// Load the selected provider's catalog on composer mount or explicit retry.
// Re-read choices after the real catalog service loads; catalog display DTOs are not
// interchangeable with the session-scoped choices used to validate Send.
export async function activateSessionModels(epoch: string, sessionId: string,
  read: () => Promise<SessionChoicesResponse>, load: (provider: string) => Promise<ModelCatalogModelsResponse>,
  current: () => boolean): Promise<SessionChoicesResponse> {
  const before = await read();
  if (!current()) throw new DOMException("Selection changed", "AbortError");
  if (before.status !== "ok" || before.epoch !== epoch || before.sessionId !== sessionId || !before.current)
    throw new Error("Session choices unavailable");
  if (before.models.length > 0) return before;
  const provider = before.current.providerKey;
  let catalog: ModelCatalogModelsResponse;
  try { catalog = await load(provider); }
  catch {
    if (!current()) throw new DOMException("Selection changed", "AbortError");
    // A provider timeout is not failure of the independently resolved prompt catalog.
    // Keep real observed choices; never synthesize models or image capability.
    return before;
  }
  if (!current()) throw new DOMException("Selection changed", "AbortError");
  if (catalog.epoch !== epoch || catalog.providerId !== provider)
    throw new Error("Provider catalog unavailable");
  if (catalog.status !== "ok" || catalog.availability !== "Ready") return before;
  const after = await read();
  if (!current()) throw new DOMException("Selection changed", "AbortError");
  if (after.status !== "ok" || after.epoch !== epoch || after.sessionId !== sessionId || after.current?.providerKey !== provider)
    throw new Error("Session provider changed");
  return after;
}
