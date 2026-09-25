// Test-owned RPC shape; no provider, profile, filesystem or network calls.
export const sessionOperations = {
  choices: async ({ expectedEpoch, sessionId }: { expectedEpoch: string; sessionId: string }) => {
    const fixture = (window as unknown as Window & { promptFixture: {
      readChoices: (epoch: string, sessionId: string) => Promise<unknown>;
    } }).promptFixture;
    return fixture.readChoices(expectedEpoch, sessionId);
  },
};
export const sessionUsage = { read: async () => { throw new Error("Usage is not available in this isolated fixture."); } };
