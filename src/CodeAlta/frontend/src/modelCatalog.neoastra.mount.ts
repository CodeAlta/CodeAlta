// Test-owned RPC shape. No provider, profile or network calls.
export const sessionOperations = {
  choices: async ({ expectedEpoch, sessionId }: { expectedEpoch: string; sessionId: string }) => {
    const fixture = (window as unknown as Window & { catalogFixture: {
      readChoices: (epoch: string, sessionId: string) => Promise<unknown>;
    } }).catalogFixture;
    return fixture.readChoices(expectedEpoch, sessionId);
  },
};
export const sessionUsage = { read: async () => { throw new Error("Usage is not available in this isolated fixture."); } };
