// Isolated project-opening bridge; no native host or user catalog is touched.
import type { WorkspaceDirectoryCompletionRequest, WorkspaceDirectoryCompletionResponse, WorkspaceOpenProjectRequest,
  WorkspaceOpenProjectResponse, WorkspaceProject, WorkspaceSnapshot } from "#neoastra";
import { workspace as fixtureWorkspace } from "./settingsShell.neoastra.mount";
export * from "./settingsShell.neoastra.mount";
// Optional shell/settings services remain unavailable, as in the browser demo.
export { neoRpcContractHash, startupConfig, gitIssues, pluginUi, agentPrompts, mcpServers, plugins, skills,
  globalConfig, providerLogin, appUpdate, reminder } from "./demo-api";
import { desktopShell as demoShell } from "./demo-api";

const imported: WorkspaceProject[] = [];
const imports: WorkspaceOpenProjectRequest[] = [];
// The folder dialog of the test: it answers with `pick` and records what it was asked.
const fixture: { imports: WorkspaceOpenProjectRequest[]; pick: { status: string; path: string | null };
  picks: { title: string | null; initialDirectory: string | null }[] } = { imports, pick: { status: "unavailable", path: null }, picks: [] };
Object.assign(window, { projectFocusFixture: fixture });
export const desktopShell = {
  ...demoShell,
  pickFolder: async (request: { title: string | null; initialDirectory: string | null }) => { fixture.picks.push(request); return fixture.pick; },
};
export const workspace = {
  ...fixtureWorkspace,
  snapshot: async (): Promise<WorkspaceSnapshot> => {
    const snapshot = await fixtureWorkspace.snapshot() as WorkspaceSnapshot;
    return { ...snapshot, projects: [...snapshot.projects, ...imported] };
  },
  completeDirectory: async (request: WorkspaceDirectoryCompletionRequest): Promise<WorkspaceDirectoryCompletionResponse> => ({
    status: "complete", hostEpoch: request.expectedHostEpoch, directoryPath: request.directoryPath, prefix: request.prefix,
    directories: [], entriesVisited: 0, omittedUnsafeEntries: false,
  }),
  openProject: async (request: WorkspaceOpenProjectRequest): Promise<WorkspaceOpenProjectResponse> => {
    imports.push(request);
    if (request.confirmed) imported.push({ id: "imported", name: "Imported project", path: request.directoryPath, archived: false });
    return { status: request.confirmed ? "ok" : "confirmation_required", hostEpoch: request.expectedHostEpoch,
      requestedPath: request.directoryPath, projectPath: request.directoryPath, projectId: request.confirmed ? "imported" : null };
  },
};
