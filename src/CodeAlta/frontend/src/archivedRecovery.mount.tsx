import { useLayoutEffect, useRef, useState } from "react";
import { createRoot } from "react-dom/client";
import type { WorkspaceSession, WorkspaceSnapshot } from "#neoastra";
import { SessionComposerGate, ReminderScopeGate } from "./ArchivedScopeGates";
import { ArchivedActionRecovery } from "./ArchivedActionRecovery";
import { OwnedSessionPanel } from "./OwnedSessionPanel";
import { ReadOnlyComposer } from "./ReadOnlyComposer";
import { createMutationCapability, createOwnedSubmissions } from "./sessionOperations";
import { createSteeringSubmissions } from "./sessionSteering";
import { createCompactionSubmissions } from "./sessionCompaction";
import { createAbortRunSubmissions } from "./sessionAbortRun";
import { createQueueSubmissions } from "./sessionQueue";
import { createReminderActions } from "./reminderActions";
import { createRuntimeStateReader } from "./runtimeState";
import { createDraftIndicators } from "./promptDraft";
import { createNextSendSelectionStore } from "./nextSendSelection";
import { dispatchWorkspaceShortcut, type WorkspaceShortcutState } from "./workspaceShortcutDispatch";
import { activateContextShortcut } from "./contextShortcut";
import { createAskActions, captureAskAction } from "./sessionAsks";
import { createUserInputReviewer } from "./sessionUserInput";
import { createPermissionReviewer, type PermissionReviewState } from "./sessionPermissions";
import type { SessionPermissionCommand, SessionPermissionResolution } from "#neoastra";
import { ShellLanguageContext } from "./shellLanguage";
import { translate, type Locale } from "./localization";

const epoch = "12345678-1234-1234-1234-123456789abc";
const sessions: WorkspaceSession[] = ["one", "two"].map(id => ({ createdAt: null, id, title: id, fullTitle: id, fullTitleTruncated: false,
  parentSessionId: null, scopeKind: "project", projectId: "project", lineageIssue: null,
  workspacePath: "/fixture/project", providerKey: "fixture", updatedAt: "2026-09-24T00:00:00Z" }));
const catalog: WorkspaceSnapshot = { configured: true, projects: [{ id: "project", name: "Project", path: "/fixture/project", archived: false }],
  sessions, projectsTruncated: false, sessionsTruncated: false, displayTextTruncated: false };
const calls: { kind: string; request: unknown; resolve: (value: unknown) => void; reject: (error: Error) => void }[] = [];
function hold(kind: string, request: unknown): Promise<never> {
  return new Promise((resolve, reject) => calls.push({ kind, request, resolve: resolve as (value: unknown) => void, reject }));
}
const capability = createMutationCapability(epoch);
const submissions = createOwnedSubmissions(request => hold("send", request), request => hold("abort", request));
const steering = createSteeringSubmissions(request => hold("steer", request));
const compaction = createCompactionSubmissions(request => hold("compact", request));
const abortRuns = createAbortRunSubmissions(request => hold("cancel", request));
const queue = createQueueSubmissions(request => hold("queue", request), request => hold("cancelQueue", request));
const asks = createAskActions(request => hold("answerAsk", request), request => hold("cancelAsk", request));
const reads: string[] = [];
const inputHandle = (sessionId: string) => ({ operationId: "33333333-3333-3333-3333-333333333333", runtimeInstanceId: "22222222-2222-2222-2222-222222222222",
  attachmentGeneration: "5", sessionId, runId: "run-1", interactionId: `input-${sessionId}`, attemptId: "55555555-5555-5555-5555-555555555555" });
const inputs = createUserInputReviewer(async request => { reads.push(`input:${request.sessionId}`); return { status: "ok", hostEpoch: epoch, sessionId: request.sessionId,
  entries: [{ handle: inputHandle(request.sessionId), providerId: "fixture", prompts: [{ id: "q", question: "Question", header: null,
    options: [], allowFreeform: true }] }], hasMore: false }; }, request => hold("resolveInput", request), request => hold("cancelInput", request));
const permissionEntry: SessionPermissionCommand = { handle: inputHandle("one"), providerId: "fixture",
  command: "original command Settings Unknown Ready Copy", workingDirectory: "C:\\fixture\\Settings", reason: "Unknown Ready" };
const permissions = createPermissionReviewer(async request => { reads.push(`permission:${request.sessionId}`); return { status: "ok" as const, hostEpoch: epoch, sessionId: request.sessionId,
  entries: request.sessionId === "one" ? [permissionEntry] : [], hasMore: false }; },
  request => hold("permission", request) as Promise<SessionPermissionResolution>);
const reminders = createReminderActions(request => hold("createReminder", request), request => hold("deleteReminder", request), undefined,
  request => hold("save", request));
const runtimeReads: unknown[] = [];
const runtimeReader = createRuntimeStateReader(async request => {
  runtimeReads.push(request);
  return { status: "ok", hostEpoch: epoch, sessionId: request.sessionId,
    runtimeInstanceId: "fixture-runtime", coordinatorTransitionInProgress: false, entry: {
      attachmentGeneration: "5", activeRunId: null, isRetiring: false, isTerminated: false, queueDrainInProgress: false,
      providerId: "Settings", providerKey: "Ready", modelId: "Unknown", reasoningEffort: "Copy",
      agentPromptId: "Send", pendingAgentPromptId: "Abort", activity: null,
    } };
});
const drafts = createDraftIndicators();
const selections = createNextSendSelectionStore(key => localStorage.getItem(key), (key, value) => localStorage.setItem(key, value));
let setArchived: (value: boolean) => void = () => {};
let setSession: (value: string) => void = () => {};
let setHost: (value: string | null) => void = () => {};
let setView: (value: "workspace" | "reminders") => void = () => {};
let setLanguage: (value: Locale) => void = () => {};
const fixture = { calls, reads, runtimeReads,
  language: (value: Locale) => setLanguage(value),
  revoke: () => capability.observe({ status: "stale_epoch", epoch }),
  draftVisible: (id: string, selected: string | null) => drafts.visible(id, selected),
  archive: (value: boolean) => setArchived(value), session: (value: string) => setSession(value),
  host: (value: string | null) => setHost(value), view: (value: "workspace" | "reminders") => setView(value),
  ask: (sessionId: string, kind: "answer" | "cancel") => asks.submit(kind, captureAskAction(epoch, {
    operationId: "33333333-3333-3333-3333-333333333333", runtimeInstanceId: "22222222-2222-2222-2222-222222222222",
    attachmentGeneration: "5", providerId: "fixture", sessionId, runId: "run-1",
    askId: sessionId === "one" ? "66666666-6666-6666-6666-666666666666" : "77777777-7777-7777-7777-777777777777", responseGeneration: "1",
  }, kind === "answer" ? [{ questionIndex: 0, freeformText: `Original ${sessionId} answer` }] : [],
  sessionId === "one" ? "88888888-8888-8888-8888-888888888888" : "99999999-9999-9999-9999-999999999999"), () => true),
  input: async (sessionId: string, action: "resolve" | "cancel") => {
    const selected = inputs.forSelection(epoch, sessionId, new AbortController().signal, () => {}, () => {});
    await selected.refresh();
    void (action === "resolve" ? selected.resolve(inputHandle(sessionId), [{ promptId: "q", value: `Original ${sessionId} input` }])
      : selected.cancel(inputHandle(sessionId)));
  },
  permission: async () => {
    let ready: PermissionReviewState | undefined;
    const selected = permissions.forSelection({ expectedHostEpoch: epoch, sessionId: "one" }, new AbortController().signal, state => { ready = state; });
    await selected.refresh();
    if (ready?.kind !== "ready") throw new Error("Permission fixture not ready");
    void selected.decide(ready.entries[0], "allow_once");
  },
  save: (sessionId: string) => reminders.submit({ epoch, sessionId }, { expectedEpoch: epoch, sessionId,
    reminderId: "reminder-1", editRevision: "7", content: `Saved exact message for ${sessionId}` }, "save"),
  steer: (sessionId: string) => steering.submit({ expectedEpoch: epoch, sessionId, clientRequestId: `steer-${sessionId}`,
    expectedRuntimeInstanceId: "22222222-2222-2222-2222-222222222222", expectedAttachmentGeneration: "5", expectedRunId: "run-1", text: `Steer exact ${sessionId}` },
    new AbortController().signal, capability, () => {}),
  compact: (sessionId: string) => compaction.submit({ expectedEpoch: epoch, sessionId, clientRequestId: `compact-${sessionId}`,
    expectedRuntimeInstanceId: "22222222-2222-2222-2222-222222222222", expectedAttachmentGeneration: "5" }, new AbortController().signal, capability, () => {}),
  cancel: (sessionId: string) => abortRuns.submit({ expectedEpoch: epoch, sessionId, clientRequestId: `cancel-${sessionId}`,
    expectedRuntimeInstanceId: "22222222-2222-2222-2222-222222222222", expectedAttachmentGeneration: "5", expectedRunId: "run-1" },
    new AbortController().signal, capability, () => {}),
  queue: (sessionId: string) => queue.submit({ expectedEpoch: epoch, sessionId, clientRequestId: `queue-${sessionId}`,
    expectedRuntimeInstanceId: "22222222-2222-2222-2222-222222222222", expectedAttachmentGeneration: "5", text: `Queue exact ${sessionId}` },
    new AbortController().signal, capability, () => {}),
  abort: (sessionId: string) => submissions.abort({ sessionId, request: { expectedEpoch: epoch,
    clientRequestId: `abort-${sessionId}`, targetOperationId: "33333333-3333-3333-3333-333333333333" } },
    new AbortController().signal, capability, () => {}),
  cancelQueue: (sessionId: string) => queue.cancel({ sessionId, request: { expectedEpoch: epoch,
    clientRequestId: `cancel-queue-${sessionId}`, targetOperationId: "44444444-4444-4444-4444-444444444444" } },
    new AbortController().signal, capability, () => {}),
};
Object.assign(window, { archivedRecoveryFixture: fixture });

function App() {
  const [locale, language] = useState<Locale>("en");
  setLanguage = language;
  const [archived, archive] = useState(false);
  const [sessionId, session] = useState("one");
  const [host, hostChange] = useState<string | null>(epoch);
  const [view, navigate] = useState<"workspace" | "reminders">("workspace");
  const trigger = useRef<HTMLButtonElement>(null);
  const shell = useRef<HTMLDivElement>(null);
  const chord = useRef<WorkspaceShortcutState>({ chordPending: false, sessionInfoPrefix: null, reminderPrefix: null });
  setArchived = archive; setSession = session; setHost = hostChange; setView = navigate;
  const snapshot = { ...catalog, projects: [{ ...catalog.projects[0], archived }] };
  const selected = snapshot.sessions.find(row => row.id === sessionId);
  const current = host === epoch && selected;
  useLayoutEffect(() => {
    const listener = (event: KeyboardEvent) => dispatchWorkspaceShortcut(event, chord.current, {
      workspaceActive: view === "workspace", workspaceShell: shell.current, modalOpen: false,
      selectedProjectFocused: false, infoTrigger: null, reminderTrigger: null, compactTrigger: trigger.current,
      infoSelection: selected ? { sessionId, projectId: "project" } : null,
      selection: current && !archived ? { epoch, sessionId, projectId: "project" } : null,
      run: action => { if (action === "context") activateContextShortcut(shell.current);
        if (action === "compact" && !archived && current && trigger.current?.isConnected && !trigger.current.disabled)
        trigger.current.click(); },
    });
    window.addEventListener("keydown", listener);
    return () => window.removeEventListener("keydown", listener);
  }, [archived, view, current, selected, sessionId]);
  return <ShellLanguageContext.Provider value={{ locale, choice: locale, setLanguage: () => {} }}><div ref={shell} className="session-workspace" data-archived={archived} data-session={sessionId} data-locale={locale}>
    <button type="button" onClick={() => navigate("workspace")}>Workspace</button>
    <button type="button" onClick={() => navigate("reminders")}>Reminders</button>
    {view === "workspace" && selected && <SessionComposerGate snapshot={snapshot} projectId="project" session={selected}
      epoch={current ? epoch : null} owned={current ? <OwnedSessionPanel sessionId={sessionId} epoch={epoch} projectId="project"
        submissions={submissions} steering={steering} compaction={compaction} abortRuns={abortRuns} queue={queue}
        capability={capability} runtimeReader={runtimeReader} permissionReviewer={null} draftIndicators={drafts}
        selections={selections} compactTrigger={trigger} /> : null}
      readOnly={<ReadOnlyComposer key={sessionId} sessionId={sessionId} provider="fixture" draftIndicators={drafts}
        reason={archived ? translate(locale, "Archived project; this session is read-only. Sending is unavailable.") : undefined} />}
      recovery={current ? <ArchivedActionRecovery epoch={epoch} sessionId={sessionId} submissions={submissions}
        steering={steering} compaction={compaction} abortRuns={abortRuns} queue={queue} asks={asks} inputs={inputs} permissions={permissions} /> : null} />}
    {view === "reminders" && <ReminderScopeGate snapshot={snapshot} projectId="project" session={current || undefined}
      epoch={current ? epoch : null} mutationAllowed={capability.canMutate()} canMutate={capability.canMutate}
      read={async request => ({ status: "ok", epoch: request.expectedEpoch, sessionId: request.sessionId,
        activeCount: 0, completedCount: 0, reminders: [] })}
      readDetail={async request => ({ status: "missing_reminder", epoch: request.expectedEpoch, sessionId: request.sessionId,
        reminderId: request.reminderId, content: null, delaySeconds: null, repeatCount: null, editRevision: null })}
      actions={reminders} />}
  </div></ShellLanguageContext.Provider>;
}
createRoot(document.getElementById("app")!).render(<App />);
