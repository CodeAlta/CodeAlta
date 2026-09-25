// Isolated list transport for the mounted production AskPanel. No host or provider access.
export const sessionAsks = {
  list: (request: unknown) => (window as Window & { askFixtureList?: (request: unknown) => Promise<unknown> }).askFixtureList?.(request)
    ?? Promise.reject(new Error("Fixture list transport is not mounted")),
  observe: async () => { throw new Error("Observation is not available in this fixture"); },
};
