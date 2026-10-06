// Only explicit test-owned choices and receipt reads are reachable in this browser fixture;
// every other service is the inert one of the browser demo.
export * from "./demo-api";
export const sessionOperations = { choices: async ({ expectedEpoch, sessionId }: { expectedEpoch: string; sessionId: string }) => {
  const fixture = window as Window & { fixtureChoiceReads?: number };
  fixture.fixtureChoiceReads = (fixture.fixtureChoiceReads ?? 0) + 1;
  return {
  ...((window as Window & { fixtureChoicesFail?: boolean }).fixtureChoicesFail
    ? { status: "read_failed", current: null, prompts: [], models: [] }
    : { status: "ok", current: { providerKey: "fixture-provider", agentPromptId: "default", modelId: "fixture-model", reasoningEffort: "medium" },
      prompts: [{ id: "default", name: "Default agent" }], models: [{ id: "fixture-model", name: "Fixture model", efforts: ["low", "medium", "high"], imageInput: null, startEffort: "high" }] }),
  epoch: expectedEpoch, sessionId,
}; }, receipts: async ({ expectedEpoch }: { expectedEpoch: string }) => {
  const fixture = window as Window & { fixtureReceiptRows?: unknown[]; fixtureReceiptReads?: number };
  fixture.fixtureReceiptReads = (fixture.fixtureReceiptReads ?? 0) + 1;
  return { status: "ok", epoch: expectedEpoch, rows: fixture.fixtureReceiptRows ?? [], next: null };
} };
