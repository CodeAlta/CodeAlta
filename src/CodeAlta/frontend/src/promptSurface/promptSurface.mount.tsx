// Test-owned mount of the production prompt surface: the owned composer with the chrome the window gives it (the
// Workspace chip, the MCP status, the sub-agents at work) against a scripted host. `fixture.split(true)` shows two
// panes side by side, as a split view does.
import { createElement, useSyncExternalStore } from "react";
import { createRoot } from "react-dom/client";
import { OwnedSessionPanel } from "../OwnedSessionPanel";
import { ComposerChrome } from "../composerChrome";
import { ComposerStatus } from "../ComposerStatus";
import { createMutationCapability, createOwnedSubmissions } from "../sessionOperations";
import { createSteeringSubmissions } from "../sessionSteering";
import { createCompactionSubmissions } from "../sessionCompaction";
import { createAbortRunSubmissions } from "../sessionAbortRun";
import { createQueueSubmissions } from "../sessionQueue";
import { createRuntimeStateReader } from "../runtimeState";
import { createDraftIndicators } from "../promptDraft";
import { createNextSendSelectionStore } from "../nextSendSelection";
import { SubAgentsChip } from "./SubAgentsChip";
import { WorkspaceChip } from "./WorkspaceChip";
import type { SubSessionAtWork } from "../spaces/spaces";
import type { SessionAdmission, SessionRuntimeBackgroundTaskResponse, SessionRuntimeStateResponse } from "#neoastra";

const unavailable = async (): Promise<never> => { throw new Error("The prompt surface fixture does not offer this operation"); };
const epoch = "fixture-epoch";
const runtime = "11111111-1111-4111-8111-111111111111";
let run: string | null = null;
let background: SessionRuntimeBackgroundTaskResponse[] = [];
let operations = 0;
const operationId = () => `22222222-2222-4222-8222-${String(++operations).padStart(12, "0")}`;
const listeners = new Set<() => void>();
let state = { split: false, subAgents: [] as SubSessionAtWork[] };
const change = (next: Partial<typeof state>) => { state = { ...state, ...next }; listeners.forEach(listener => listener()); };

const submissions = createOwnedSubmissions(async (request): Promise<SessionAdmission> => {
  run = "run-1";
  return { status: "accepted", epoch: request.expectedEpoch, receipt: { kind: "Send", clientRequestId: request.clientRequestId,
    sessionId: request.sessionId, operationId: operationId(), targetOperationId: null, state: "pending", outcome: null, code: null, runId: null, queueInsertion: null } };
}, unavailable);
const shared = {
  epoch, submissions, steering: createSteeringSubmissions(unavailable), queue: createQueueSubmissions(unavailable, unavailable),
  compaction: createCompactionSubmissions(unavailable), abortRuns: createAbortRunSubmissions(unavailable),
  capability: createMutationCapability(epoch), draftIndicators: createDraftIndicators(), permissionReviewer: null,
  selections: createNextSendSelectionStore(key => localStorage.getItem(key), (key, value) => localStorage.setItem(key, value)),
  runtimeReader: createRuntimeStateReader(async request => ({ status: "ok", hostEpoch: request.expectedHostEpoch, sessionId: request.sessionId,
    entry: { attachmentGeneration: "12", activeRunId: run, isRetiring: false, isTerminated: false, backgroundTasks: background, queueDrainInProgress: false,
      providerId: "fixture-provider", providerKey: "fixture-provider", modelId: null, reasoningEffort: null, agentPromptId: null, pendingAgentPromptId: "default", activity: null },
    runtimeInstanceId: runtime, coordinatorTransitionInProgress: false } satisfies SessionRuntimeStateResponse)),
  onOpenReminders: () => { }, activeReminderCount: 2, onOpenCatalog: () => { },
  archive: { asks: false, run: () => { archives++; } },
};
let archives = 0;
const project = { id: "project-1", name: "CodeAlta", path: "C:\\Users\\alexa\\.alta\\worktrees\\codealta\\mellow-chisel-pz93" };
const readGit = async () => ({ status: "ok", projectId: project.id, branch: "alta/mellow-chisel-pz93", detached: false, insertions: 94, deletions: 32, changedFiles: 7 });
const readStatus = async ({ projectId }: { projectId: string | null }) => ({ status: "ok", projectId,
  items: [{ pluginId: "mcp", name: "mcp-status", label: "MCP", text: "1/1 · tools not loaded", tone: "warning", settingsPage: "mcp" }] });

function Pane({ sessionId }: { sessionId: string }) {
  const chrome = { context: <WorkspaceChip epoch={epoch} project={project} read={readGit} branches={false}
      worktree={{ path: project.path, name: "mellow-chisel-pz93", missing: false }} onShowChanges={() => { }} onOpenTerminal={() => { }} />,
    status: <ComposerStatus epoch={epoch} projectId={project.id} sessionId={sessionId} read={readStatus} />,
    subAgents: state.subAgents.length ? <SubAgentsChip sessions={state.subAgents} /> : undefined };
  // The composer at the bottom of its pane, as in the window.
  return <div className="session-workspace" style={{ flex: "1 1 0", minWidth: 0, alignSelf: "stretch", display: "flex", flexDirection: "column", justifyContent: "flex-end" }}>
    <div className="composer-region"><ComposerChrome.Provider value={chrome}>
      <OwnedSessionPanel {...shared} sessionId={sessionId} infoControl={<button type="button" className="bp6-button bp6-minimal session-info-trigger">Session info</button>}
        usageTarget={{ epoch, sessionId, createdAt: "2026-10-15T09:00:00Z", scope: "project", projectId: project.id, expectedProjectPath: project.path }} />
    </ComposerChrome.Provider></div>
  </div>;
}

function Fixture() {
  useSyncExternalStore(listener => { listeners.add(listener); return () => listeners.delete(listener); }, () => state);
  return <div id="workspace-shell" className="ide-shell" style={{ display: "flex", gap: 8, alignItems: "flex-end", padding: 8, height: "100vh", boxSizing: "border-box" }}>
    <Pane sessionId="fixture-session" />
    {state.split && <Pane sessionId="fixture-session-2" />}
  </div>;
}

const fixture = {
  startRun() { run = "run-1"; },
  finishRun() { run = null; },
  background(states: string[]) {
    background = states.map((value, index) => ({ taskId: `job-${index}`, kind: "command", description: `Background command ${index}`,
      toolCallId: null, startedAt: new Date(Date.now() - 5_000).toISOString(), state: value, isJob: true,
      exitCode: null, endedAt: value === "running" ? null : new Date().toISOString() }));
  },
  subAgents(count: number, waiting = 0) {
    change({ subAgents: Array.from({ length: count }, (_, index) => ({ sessionId: `child-${index}`, title: `Sub-agent ${index + 1}`,
      state: index < waiting ? "waiting" as const : "running" as const })) });
  },
  split(value: boolean) { change({ split: value }); },
  /** How many times Archive was chosen in the `⋯` menu. */
  archives: () => archives,
};
Object.assign(window, { fixture });
createRoot(document.getElementById("app")!).render(createElement(Fixture));
