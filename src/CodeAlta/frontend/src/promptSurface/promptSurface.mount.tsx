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
import { createExternalOpener, ExternalOpenerContext } from "../externalEditors/externalEditors";
import { Button } from "@blueprintjs/core";
import { AppIcon } from "../AppIcon";
import { ComposerSelectionFields, SendSplitButton } from "../ComposerSurface";
import { ReadOnlyComposer } from "../ReadOnlyComposer";
import { ProviderBrandsContext, type ProviderBrands } from "../ProviderIcon";
import { SpeechContext, type Speech } from "../speech/speech";
import { createFixtureHost, createFixtureMicrophone } from "../speech/speechFixture";
import { useSpeechInstall } from "../speech/useSpeechInstall";
import type { SubSessionAtWork } from "../spaces/spaces";
import type { WorkPlaceApi } from "../worktrees/WorkPlaceChoice";
import type { WorkPlace } from "../worktrees/worktrees";
import type { SessionAdmission, SessionRuntimeBackgroundTaskResponse, SessionRuntimeStateResponse } from "#neoastra";

const unavailable = async (): Promise<never> => { throw new Error("The prompt surface fixture does not offer this operation"); };
const epoch = "fixture-epoch";
const runtime = "11111111-1111-4111-8111-111111111111";
let run: string | null = null;
let background: SessionRuntimeBackgroundTaskResponse[] = [];
let operations = 0;
const operationId = () => `22222222-2222-4222-8222-${String(++operations).padStart(12, "0")}`;
const listeners = new Set<() => void>();
let state = { split: false, subAgents: [] as SubSessionAtWork[], draft: false, place: { worktree: false, base: null } as WorkPlace };
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

// What a new worktree can start from and where it goes.
const placeApi = {
  branches: async () => ({ status: "ok", busy: false, branches: [{ name: "main", current: true, remote: false, worktree: null, worktreeName: null }] }),
  list: async ({ projectId }: { projectId: string }) => ({ status: "ok", projectId, worktrees: [], newFolder: "C:\\Users\\alexa\\.alta\\worktrees\\codealta" }),
} as unknown as WorkPlaceApi;

// The prompts of the new sessions, kept as the window keeps its local drafts.
const drafts = new Map<string, string>();
const draftListeners = new Set<() => void>();
const draftOptions = <ComposerSelectionFields sessionId="draft" agent={<span />} provider={<span />} model={<span />} reasoning={<span />}
  summary={{ agent: "Default agent", provider: "Claude Code", providerKey: "claude-code", model: "Fixture model", reasoning: "high" }} />;
// The providers of the configuration: the session's is a second account of Claude Code, named in Settings.
const brands: ProviderBrands = new Map([["fixture-provider", { key: "fixture-provider", type: "claude-code", name: "Claude Business" }],
  ["claude-code", { key: "claude-code", type: "claude-code", name: "Claude Code" }]]);;
let starts = 0;
let changesShown = 0, editorsOpened = 0;
// The external editor of a machine that has Visual Studio Code: what the window asks to open is recorded.
const opened: string[] = [];
const externalOpener = createExternalOpener({
  list: async () => ({ status: "ok", platform: "windows", chosen: null, customProgram: null, customArguments: null,
    editors: [{ id: "vscode", name: "Visual Studio Code", program: "Code.exe" }], current: { id: "vscode", name: "Visual Studio Code", program: "Code.exe" } }),
  openFolder: async (target, projectId, sessionId) => { opened.push(`${target} ${projectId} ${sessionId}`); return "ok"; },
  openEntry: async () => "ok", openWorktree: async () => "ok", failed: () => { },
});

// Speech to prompt against a host and a microphone the test plays: the model is in the cache and the microphone was chosen.
const speechHost = createFixtureHost({ installed: ["parakeet-tdt-0.6b-v3"] });
const speechMicrophone = createFixtureMicrophone();

function Pane({ sessionId }: { sessionId: string }) {
  const active = sessionId === "fixture-session";
  // A session in a worktree, or with `fixture.draft(true)` the composer of a session that does not exist yet, whose chip chooses where it will work.
  const chrome = { context: state.draft
      ? <WorkspaceChip epoch={epoch} project={project} read={readGit} branches={false} onShowChanges={() => { }}
        place={{ value: state.place, onChange: place => change({ place }), onOpenSettings: () => { }, api: placeApi }} />
      : <WorkspaceChip epoch={epoch} project={project} sessionId={sessionId} read={readGit} branches={false}
        worktree={{ path: project.path, name: "mellow-chisel-pz93", missing: false }} onShowChanges={() => { changesShown++; }} onOpenEditor={() => { editorsOpened++; }}
        onOpenTerminal={() => { }} />,
    status: <ComposerStatus epoch={epoch} projectId={project.id} sessionId={sessionId} read={readStatus} />,
    subAgents: state.subAgents.length ? <SubAgentsChip sessions={state.subAgents} /> : undefined };
  // The composer at the bottom of its pane, as in the window.
  return <div className="session-workspace" style={{ flex: "1 1 0", minWidth: 0, alignSelf: "stretch", display: "flex", flexDirection: "column", justifyContent: "flex-end" }}>
    <div className="composer-region"><ComposerChrome.Provider value={chrome}>
      {state.draft
        // The composer of a new session, as the window gives it: its Agent chip, its meters and Start session.
        ? <ReadOnlyComposer key="draft" sessionId={`draft-${sessionId}`} provider={null} draftIndicators={shared.draftIndicators} active={active}
          reason="Draft kept locally. Start a session to send it."
          localDraft={{ read: () => drafts.get(sessionId) ?? "", subscribe: listener => { draftListeners.add(listener); return () => draftListeners.delete(listener); },
            edit: text => { drafts.set(sessionId, text); draftListeners.forEach(listener => listener()); }, options: draftOptions,
            disabled: false, busy: false, submit: () => { starts++; },
            surface: { epoch, onOpenProviders: () => { }, contextTokens: 200_000 },
            action: <SendSplitButton enqueue={false} onEnqueueChange={() => { }} enqueueDisabled>
              <Button intent="primary" icon={<AppIcon name="send" size={16} />} aria-label="Start session" onClick={() => { starts++; }} />
            </SendSplitButton> }} />
        : <OwnedSessionPanel {...shared} key="session" sessionId={sessionId} active={active} infoControl={<button type="button" className="bp6-button bp6-minimal session-info-trigger">Session info</button>}
          usageTarget={{ epoch, sessionId, createdAt: "2026-10-15T09:00:00Z", scope: "project", projectId: project.id, expectedProjectPath: project.path }} />}
    </ComposerChrome.Provider></div>
  </div>;
}

function Fixture() {
  useSyncExternalStore(listener => { listeners.add(listener); return () => listeners.delete(listener); }, () => state);
  const speech = useSpeechInstall(speechHost.api, speechHost.epoch, "en", { microphones: speechMicrophone.microphones, silenceAfter: 300, sendWait: 1_500 });
  fixture.speech = speech;
  return <SpeechContext.Provider value={speech}><ProviderBrandsContext.Provider value={brands}><ExternalOpenerContext.Provider value={externalOpener}>
    <div id="workspace-shell" className="ide-shell" style={{ display: "flex", gap: 8, alignItems: "flex-end", padding: 8, height: "100vh", boxSizing: "border-box" }}>
      <Pane sessionId="fixture-session" />
      {state.split && <Pane sessionId="fixture-session-2" />}
    </div>
  </ExternalOpenerContext.Provider></ProviderBrandsContext.Provider></SpeechContext.Provider>;
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
  /** Shows the composer of a new session, which works in the folder of the project until a worktree is chosen in its chip. */
  draft(value: boolean) { change({ draft: value, place: { worktree: false, base: null } }); },
  /** Speech to prompt: its state, the host (`tell` a download) and the microphone. */
  speech: null as Speech | null, speechHost, speechMicrophone,
  /** How many times the chip opened the changes, the code editor, and what it opened outside. */
  changesShown: () => changesShown, editorsOpened: () => editorsOpened, opened: () => [...opened],
  /** How many times a new session was started. */
  starts: () => starts,
  /** How many times Archive was chosen in the `⋯` menu. */
  archives: () => archives,
};
Object.assign(window, { fixture });
createRoot(document.getElementById("app")!).render(createElement(Fixture));
