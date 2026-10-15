// Only explicit test-owned choices and receipt reads are reachable in this browser fixture;
// every other service is the inert one of the browser demo.
export * from "./demo-api";
/**
 * What a test of the permission mode sets on the window before the page loads: the provider then offers modes, and
 * the session holds `effort` (one its model may not list) and `mode` (null for the one of its provider). The host
 * answers a change of mode with `status`, and `calls` are the modes it was asked for.
 */
type PermissionFixture = { effort: string; mode: string | null; status: string; calls: string[] };
const permission = () => (window as Window & { fixturePermission?: PermissionFixture }).fixturePermission;
export const sessionOperations = { choices: async ({ expectedEpoch, sessionId }: { expectedEpoch: string; sessionId: string }) => {
  const fixture = window as Window & { fixtureChoiceReads?: number };
  fixture.fixtureChoiceReads = (fixture.fixtureChoiceReads ?? 0) + 1;
  const modes = permission();
  return {
  ...((window as Window & { fixtureChoicesFail?: boolean }).fixtureChoicesFail
    ? { status: "read_failed", current: null, prompts: [], models: [] }
    : { status: "ok", current: { providerKey: "fixture-provider", agentPromptId: "default", modelId: "fixture-model", reasoningEffort: modes?.effort ?? "medium",
        ...(modes ? { permissionMode: modes.mode } : {}) },
      prompts: [{ id: "default", name: "Default agent" }, ...(modes ? [{ id: "plan", name: "Plan agent" }] : [])],
      // A model whose provider says it has no reasoning effort (Haiku of Claude Code), when the test asks for one.
      models: [(window as Window & { fixtureNoReasoning?: boolean }).fixtureNoReasoning
        ? { id: "fixture-model", name: "Fixture model", efforts: [], effortsComplete: true, imageInput: null, startEffort: null }
        : { id: "fixture-model", name: "Fixture model", efforts: ["low", "medium", "high"], imageInput: null, startEffort: "high" }],
      ...(modes ? { permissionModes: [{ id: "default" }, { id: "acceptEdits" }, { id: "bypassPermissions" }], defaultPermissionMode: "bypassPermissions" } : {}),
      // The provider of the session was taken out of its space.
      ...((window as Window & { fixtureOutsideSpace?: boolean }).fixtureOutsideSpace ? { providerOutsideSpace: true } : {}) }),
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
