// Only the choices route is reachable in this read-only browser fixture.
export const sessionOperations = { choices: async ({ expectedEpoch, sessionId }: { expectedEpoch: string; sessionId: string }) => ({
  ...((window as Window & { fixtureChoicesFail?: boolean }).fixtureChoicesFail
    ? { status: "read_failed", current: null, prompts: [], models: [] }
    : { status: "ok", current: { providerKey: "fixture-provider", agentPromptId: "default", modelId: "fixture-model", reasoningEffort: "medium" },
      prompts: [{ id: "default", name: "Default agent" }], models: [{ id: "fixture-model", name: "Fixture model", efforts: ["low", "medium", "high"] }] }),
  epoch: expectedEpoch, sessionId,
}) };
