// Isolated project-opening bridge; no native host or user catalog is touched.
import type { WorkspaceDirectoryCompletionRequest, WorkspaceDirectoryCompletionResponse, WorkspaceOpenProjectRequest,
  WorkspaceOpenProjectResponse, WorkspaceProject, WorkspaceSnapshot } from "#neoastra";
import { workspace as fixtureWorkspace } from "./settingsShell.neoastra.mount";
export * from "./settingsShell.neoastra.mount";
// Optional shell/settings services remain unavailable, as in the browser demo.
export { neoRpcContractHash, startupConfig, gitIssues, pluginUi, agentPrompts, mcpServers, plugins, skills,
  globalConfig, providerLogin, providerUsage, appUpdate, reminder, colorSchemes, terminals, automations, workItems, issues, pullRequestPrompts, worktrees, mcpHost } from "./demo-api";
import { desktopShell as demoShell } from "./demo-api";

const imported: WorkspaceProject[] = [];
const imports: WorkspaceOpenProjectRequest[] = [];
// The folder dialog of the test: it answers with `pick` and records what it was asked.
type ShellNotice = { kind: string; runningSessions: number; busyTerminals: number };
const fixture: { imports: WorkspaceOpenProjectRequest[]; pick: { status: string; path: string | null };
  picks: { title: string | null; initialDirectory: string | null }[];
  /** What the page asked of the shell, in order, and the notice the test sends as the host does. */
  shell: string[]; notify: (notice: ShellNotice) => void } = { imports, pick: { status: "unavailable", path: null }, picks: [], shell: [], notify: () => { } };
Object.assign(window, { projectFocusFixture: fixture });
// A shell where the application can keep running without its window, and nothing is remembered yet.
const closing = { onClose: "ask" };
const preferences = () => ({ status: "ok", onClose: closing.onClose, canKeepRunning: true, platform: "windows", entryAdded: false });
export const desktopShell = {
  ...demoShell,
  preferences: async () => preferences(),
  setOnClose: async (request: { onClose: string | null }) => {
    fixture.shell.push("setOnClose:" + request.onClose);
    closing.onClose = request.onClose ?? closing.onClose;
    return preferences();
  },
  hide: async () => { fixture.shell.push("hide"); return { status: "ok" }; },
  exit: async () => { fixture.shell.push("exit"); return { status: "ok" }; },
  watch: async (_request: object, options?: { signal?: AbortSignal }) => {
    const waiting: ShellNotice[] = [];
    let wake = () => { };
    fixture.notify = notice => { waiting.push(notice); wake(); };
    options?.signal?.addEventListener("abort", () => wake(), { once: true });
    return (async function* () {
      while (!options?.signal?.aborted) {
        if (waiting.length === 0) await new Promise<void>(resolve => { wake = resolve; });
        while (waiting.length > 0) yield waiting.shift()!;
      }
    })();
  },
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
