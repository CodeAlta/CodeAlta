// Isolated bridge for the real App: the selected project's session "one" has a child session, and the host takes its
// archive and says what it changed. No native bridge, plugin runtime or user catalog is touched.
import type { WorkspaceSnapshot } from "#neoastra";
import { workspace as baseWorkspace } from "../selectedSessionMenu.neoastra.mount";
export * from "../selectedSessionMenu.neoastra.mount";

const state = { archives: [] as { sessionId: string; withChildren: boolean }[] };
Object.assign(window, { composerArchiveFixture: { state } });

export const workspace = {
  ...baseWorkspace,
  snapshot: async (): Promise<WorkspaceSnapshot> => {
    const snapshot = await baseWorkspace.snapshot();
    const one = snapshot.sessions.find(session => session.id === "one")!;
    return { ...snapshot, sessions: [...snapshot.sessions, { ...one, id: "one-child", title: "Child of one", fullTitle: "Child of one", parentSessionId: "one" }] };
  },
  archiveSession: async ({ expectedHostEpoch, sessionId, withChildren }: { expectedHostEpoch: string; sessionId: string; withChildren: boolean }) => {
    state.archives.push({ sessionId, withChildren });
    return { status: "ok", hostEpoch: expectedHostEpoch, sessionId, changed: withChildren ? [sessionId, "one-child"] : [sessionId], blockingSessionId: null };
  },
};
