// The host of the prompt surface fixture: the composer's choices and receipts, plus a usage reading with a context
// window and a prompt cache. Every other service is the inert one of the browser demo.
export * from "../composer.neoastra.mount";
import { sessionOperations as composerOperations } from "../composer.neoastra.mount";

// The model of the session has a logo of its own (Claude's), apart from the one of its provider (Claude Code's).
export const sessionOperations = { ...composerOperations, choices: async (request: { expectedEpoch: string; sessionId: string }) => {
  const value = await composerOperations.choices(request);
  const opus = (id: string | null | undefined) => id === "fixture-model" ? "claude-opus-5-5" : id;
  return { ...value, current: value.current && { ...value.current, modelId: opus(value.current.modelId) },
    models: value.models.map(model => ({ ...model, id: opus(model.id)!, name: model.name === "Fixture model" ? "Opus 5.5" : model.name })) };
} };

const epoch = "fixture-epoch";
const minutes = (count: number) => new Date(Date.now() + count * 60_000).toISOString();

/** What the usage reading says, set by the test: the tokens used and the minutes left of the prompt cache (null: unknown). */
type UsageFixture = { used: string; limit: string; cacheMinutes: number | null };
const usage = () => (window as Window & { fixtureUsage?: UsageFixture }).fixtureUsage ?? { used: "563412", limit: "1000000", cacheMinutes: 37 };

export const sessionUsage = {
  read: async ({ sessionId }: { sessionId: string }) => {
    const value = usage();
    return { status: "ok", hostEpoch: epoch, sessionId, runtimeInstanceId: "11111111-1111-4111-8111-111111111111", attachmentGeneration: "12", omittedUsageEvents: "0",
      currentSession: null,
      observation: { sequence: "3", scope: "CurrentWindow", source: "LocalProviderUsage", sourceUpdatedAt: null, eventTimestamp: "2026-10-15T10:00:00Z",
        hadInvalidValues: false, hadOmittedData: false,
        window: { currentTokens: value.used, tokenLimit: value.limit, messageCount: 42, label: null, totalContextEnvelope: null, maxOutputTokens: null },
        lastOperation: null, rateLimits: null, sessionTotal: null, promptCacheExpiresAt: value.cacheMinutes === null ? null : minutes(value.cacheMinutes) } };
  },
};
