// Only explicit test-owned choices and receipt reads are reachable in this browser fixture;
// every other service is the inert one of the browser demo.
export * from "./demo-api";
import { modelCatalog as demoCatalog } from "./demo-api";
// As the host answers for a provider it does not know, such as one removed from the configuration.
export const modelCatalog = { ...demoCatalog,
  models: async ({ expectedEpoch }: { expectedEpoch: string }) => ({ status: "not_found", epoch: expectedEpoch, providerId: null, availability: "Unknown", models: [], truncated: false }) };
/**
 * What a test of the permission mode sets on the window before the page loads: the provider then offers modes, and
 * the session holds `effort` (one its model may not list) and `mode` (null for the one of its provider). The host
 * answers a change of mode with `status`, and `calls` are the modes it was asked for.
 */
type PermissionFixture = { effort: string; mode: string | null; status: string; calls: string[] };
const permission = () => (window as Window & { fixturePermission?: PermissionFixture }).fixturePermission;
/**
 * What a test of a removed provider sets on the window: the provider of the session is no longer configured and
 * `replacement` is the default provider; `switched` names the provider the host was asked to switch to.
 */
type RemovedFixture = { replacement: string; switched: string[] };
const removed = () => (window as Window & { fixtureRemoved?: RemovedFixture }).fixtureRemoved;
// The provider of the session: the removed one until the host switched it.
const provider = () => removed()?.switched.at(-1) ?? (removed() ? "fixture-removed" : "fixture-provider");
export const sessionOperations = { reviewBeforeCommit: async () => ({ status: "unavailable" }), setReviewBeforeCommit: async () => ({ status: "unavailable" }),
  providerChoices: async ({ expectedEpoch, sessionId }: { expectedEpoch: string; sessionId: string }) => ({ status: "ok", epoch: expectedEpoch, sessionId,
    runtimeInstanceId: "11111111-1111-4111-8111-111111111111", attachmentGeneration: null, providerKey: provider(), revision: "1",
    providers: [{ id: "fixture-provider", name: "Fixture Default" }] }),
  selectProvider: async ({ expectedEpoch, sessionId, providerKey, expectedProviderKey }: { expectedEpoch: string; sessionId: string; providerKey: string; expectedProviderKey: string }) => {
    if (expectedProviderKey !== provider()) return { status: "stale_selection", epoch: expectedEpoch, sessionId };
    removed()?.switched.push(providerKey);
    return { status: "ok", epoch: expectedEpoch, sessionId };
  },
  choices: async ({ expectedEpoch, sessionId }: { expectedEpoch: string; sessionId: string }) => {
  const fixture = window as Window & { fixtureChoiceReads?: number };
  fixture.fixtureChoiceReads = (fixture.fixtureChoiceReads ?? 0) + 1;
  const modes = permission();
  const gone = provider() === "fixture-removed";
  return {
  ...((window as Window & { fixtureChoicesFail?: boolean }).fixtureChoicesFail
    ? { status: "read_failed", current: null, prompts: [], models: [] }
    : gone ? { status: "ok", current: { providerKey: "fixture-removed", agentPromptId: "default", modelId: "removed-model", reasoningEffort: "medium" },
      prompts: [{ id: "default", name: "Default agent" }], models: [], providerUnavailable: true, replacementProvider: removed()!.replacement }
    : { status: "ok", current: { providerKey: "fixture-provider", agentPromptId: "default", modelId: "fixture-model", reasoningEffort: modes?.effort ?? "medium",
        ...(modes ? { permissionMode: modes.mode } : {}) },
      prompts: [{ id: "default", name: "Default agent" }, ...(modes ? [{ id: "plan", name: "Plan agent" }] : [])],
      // A model whose provider says it has no reasoning effort (Haiku of Claude Code), when the test asks for one.
      models: [(window as Window & { fixtureNoReasoning?: boolean }).fixtureNoReasoning
        ? { id: "fixture-model", name: "Fixture model", efforts: [], effortsComplete: true, imageInput: null, startEffort: null }
        : { id: "fixture-model", name: "Fixture model", efforts: ["low", "medium", "high"], imageInput: null, startEffort: "high" }],
      ...(modes ? { permissionModes: [{ id: "default" }, { id: "acceptEdits" }, { id: "bypassPermissions" }], defaultPermissionMode: "bypassPermissions" } : {}),
      // The provider of the session was taken out of its space.
      ...((window as Window & { fixtureOutsideSpace?: boolean }).fixtureOutsideSpace ? { providerOutsideSpace: true, providerName: "Fixture Business" } : {}) }),
  epoch: expectedEpoch, sessionId,
}; }, setPermissionMode: async ({ expectedEpoch, sessionId, permissionMode }: { expectedEpoch: string; sessionId: string; permissionMode: string }) => {
  // The host takes the mode at once, or refuses it: "provider" gives the session back the mode of its provider.
  const modes = permission()!;
  modes.calls.push(permissionMode);
  if (modes.status === "ok") modes.mode = permissionMode === "provider" ? null : permissionMode;
  return { status: modes.status, epoch: expectedEpoch, sessionId, permissionMode: modes.status === "ok" ? modes.mode : null };
}, receipts: async ({ expectedEpoch }: { expectedEpoch: string }) => {
  const fixture = window as Window & { fixtureReceiptRows?: unknown[]; fixtureReceiptReads?: number };
  fixture.fixtureReceiptReads = (fixture.fixtureReceiptReads ?? 0) + 1;
  return { status: "ok", epoch: expectedEpoch, rows: fixture.fixtureReceiptRows ?? [], next: null };
} };
