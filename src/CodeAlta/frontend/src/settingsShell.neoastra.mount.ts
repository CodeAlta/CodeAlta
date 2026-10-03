// Isolated bridge for mounting the actual main.tsx in a local browser test.
// No native bridge or user data is touched.
import type { HistoryRequest, HistoryResponse, SessionDisplayItem, SessionDisplayRequest, ReminderListRequest, ReminderListResponse, WorkspaceArchiveProjectRequest, SessionRuntimeScopedRequest } from "#neoastra";
import type { SessionPermissionsRequest, SessionPermissionResolveRequest } from "#neoastra";
import type { SessionRuntimeStateRequest, SessionRuntimeStateResponse } from "#neoastra";
import type { SessionReceiptRequest, SessionReceiptPage, SessionCancelQueueRequest, SessionAdmission } from "#neoastra";
import type { createUserInputReviewer } from "./sessionUserInput";
import type { TimelineHistoryResponse, HistorySourceResponse } from "#neoastra";
const epoch = "12345678-1234-1234-1234-123456789abc";
const session = { id: "one", title: "one", fullTitle: "one", fullTitleTruncated: false, createdAt: "2026-01-02T03:04:05.1234567+14:00",
  parentSessionId: null, scopeKind: localStorage.getItem("infoFixtureUnknown") === "true" ? null : "project",
  projectId: localStorage.getItem("infoFixtureUnknown") === "true" ? null
    : localStorage.getItem("infoFixtureMismatched") === "true" ? "wrong" : "project", lineageIssue: null,
  workspacePath: "/fixture/project", providerKey: "fixture", updatedAt: "2026-09-24T00:00:00Z" };
const catalog = { configured: true, projects: [{ id: "project", name: "Project", path: "/fixture/project", archived: localStorage.getItem("usageFixtureArchived") === "true" },
    ...(localStorage.getItem("settingsFixtureSecondProject") === "true" ? [{ id: "other", name: "Other project", path: "/fixture/other", archived: false }] : [])],
  sessions: [session, { ...session, id: "two", title: "two", fullTitle: "two" },
    ...(localStorage.getItem("settingsFixtureSecondProject") === "true" ? [{ ...session, id: "other-session", title: "other-session", fullTitle: "other-session", projectId: "other", workspacePath: "/fixture/other", providerKey: "other-provider" }] : []),
    ...(localStorage.getItem("infoFixtureAmbiguous") === "true" ? [{ ...session, title: "duplicate" }] : [])],
  projectsTruncated: localStorage.getItem("usageFixtureTruncated") === "true", sessionsTruncated: false, displayTextTruncated: false };
if (localStorage.getItem("ideFixtureLongLabels") === "true") {
  catalog.projects[0].name = "Literal Arbeitsbereich 日本語 <project> " .repeat(6);
  session.title = session.fullTitle = "Literal Sitzung 日本語 <session> ".repeat(6);
}
const unavailable = async () => { throw new Error("test bridge unavailable"); };
const permissionReads: Array<{ request: SessionPermissionsRequest; resolve: (value: unknown) => void }> = [];
const permissionDecisions: Array<{ request: SessionPermissionResolveRequest; resolve: (value: unknown) => void; reject: (error: Error) => void }> = [];
type InputRequest = Parameters<Parameters<typeof createUserInputReviewer>[1]>[0];
const inputReads: Array<{ request: { expectedHostEpoch: string; sessionId: string }; resolve: (value: unknown) => void }> = [];
const inputAnswers: Array<{ request: InputRequest; resolve: (value: unknown) => void; reject: (error: Error) => void }> = [];
const inputCancels: Array<{ request: Omit<InputRequest, "answers">; resolve: (value: unknown) => void; reject: (error: Error) => void }> = [];
const permissionEntry = { handle: { operationId: epoch, runtimeInstanceId: epoch, attachmentGeneration: "7", sessionId: "one",
  runId: "literal-run", interactionId: "literal-interaction", attemptId: epoch }, providerId: "fixture",
  command: "  inert original command\n<script>not markup</script>  " + "literal ".repeat(350), workingDirectory: "/fixture/project", reason: "literal permission reason" };
const inputEntry = { handle: { ...permissionEntry.handle }, providerId: "literal-input-provider", prompts: [
  { id: "choice", header: "literal header", question: "  <script>literal question</script>\n" + "question ".repeat(90), options: [{ label: "literal choice", description: "  literal description\n" }], allowFreeform: false },
  { id: "text", header: null, question: "literal text question", options: [], allowFreeform: true },
] };
const calls: string[] = [];
const rpcCalls: string[] = [];
const archives: Array<{ request: WorkspaceArchiveProjectRequest; resolve: (value: unknown) => void }> = [];
const retainedQueueCalls: Array<{ request: unknown; resolve: (value: unknown) => void; reject: (error: Error) => void }> = [];
const queueReceiptReads: Array<{ request: SessionReceiptRequest; resolve: (value: SessionReceiptPage) => void }> = [];
const retainedCancelCalls: Array<{ request: SessionCancelQueueRequest; resolve: (value: SessionAdmission) => void; reject: (error: Error) => void }> = [];
const currentReads: Array<{ request: SessionRuntimeStateRequest; signal: AbortSignal; response?: SessionRuntimeStateResponse; resolve: (value: SessionRuntimeStateResponse) => void; reject: (error: Error) => void }> = [];
let releaseReplacementCurrent = false;
function releaseCurrentCall(call: typeof currentReads[number], attachmentGeneration = "7") {
  call.response = { status: "ok", hostEpoch: call.request.expectedHostEpoch, sessionId: call.request.sessionId,
    runtimeInstanceId: epoch, coordinatorTransitionInProgress: false,
    entry: { attachmentGeneration, activeRunId: "literal-run", isTerminated: false, isRetiring: false,
      queueDrainInProgress: false, providerId: "fixture", providerKey: "fixture", modelId: null,
      reasoningEffort: null, agentPromptId: null, pendingAgentPromptId: null, activity: null } };
  call.resolve(call.response);
}
const runtimeReads: Array<{ request: SessionRuntimeScopedRequest; signal: AbortSignal; resolve: (value: unknown) => void; reject: (error: Error) => void }> = [];
const sends: unknown[] = [];
const sendFailures: Array<() => void> = [];
const displayCalls: SessionDisplayRequest[] = [];
const displayCleanup: string[] = [];
const displayAttempts: Array<{ sessionId: string; signal: AbortSignal; opened: boolean; settled: boolean; unavailable: boolean }> = [];
const lateDisplayAttempts: string[] = [];
const reminderReads: Array<{ request: ReminderListRequest; signal: AbortSignal; resolve: (value: ReminderListResponse) => void }> = [];
const reminderSaves: Array<{ request: import("#neoastra").ReminderSaveRequest; resolve: (value: import("#neoastra").ReminderMutationResponse) => void; reject: (error: Error) => void }> = [];
const reminderDetails: import("#neoastra").ReminderDetailRequest[] = [];
const reminderSaveLists: ReminderListRequest[] = [];
const reminderTrace: Array<{ kind: string; request: unknown; signal?: AbortSignal; completed: boolean; stack: string }> = [];
function traceReminder(kind: string, request: unknown, signal?: AbortSignal) {
  const entry = { kind, request, signal, completed: false, stack: new Error().stack ?? "" };
  reminderTrace.push(entry);
  return entry;
}
const notesCalls: unknown[] = [];
const projectNameReads: Array<{ request: { projectId: string; projectPath: string }; resolve: (value: unknown) => void }> = [];
const referenceReads: Array<{ request: unknown; resolve: (value: unknown) => void }> = [];
const referenceObservations: Array<{ request: { text: string }; resolve: (value: unknown) => void }> = [];
const choiceReads: Array<{ request: { expectedEpoch: string; sessionId: string }; resolve: (value: unknown) => void }> = [];
const usageReads: Array<{ request: unknown; resolve: (value: unknown) => void; reject: (error: Error) => void }> = [];
const probes: unknown[] = [];
const clearRequests: unknown[] = [];
const renameRequests: unknown[] = [];
const projectRenames: Array<{ request: { displayName: string }; resolve: (value: unknown) => void; reject: (error: Error) => void }> = [];
const deleteRequests: unknown[] = [];
const mutationReplies: Array<{ kind: "rename" | "delete"; resolve: (value: unknown) => void }> = [];
type CreateRequest = { expectedHostEpoch: string; scope: string; projectId: string | null; projectPath: string | null; title: string | null; providerId: string | null };
const creates: Array<{ request: CreateRequest; resolve: (value: unknown) => void }> = [];
const snapshots: Array<{ resolve: (value: unknown) => void; reject: (error: Error) => void }> = [];
const snapshotCalls: unknown[] = [];
const historyCalls: HistoryRequest[] = [];
const historyReads: Array<() => void> = [];
const fileDetails = JSON.stringify({ changes: [
  { path: "<script>literal</script>.ts", kind: { type: "update" }, diff: "@@ -1 +1 @@\n-old\n+<b>literal</b>\n" },
  { path: "second.ts", operation: "unknown", diff: "binary unknown" },
] });
function fileHistory(request: HistoryRequest): HistoryResponse {
  return { status: "ok", next: null, tailOmitted: false, entries: [
    { offset: "1", eventType: "activity", kind: "FileChange", providerId: "fixture", sessionId: request.sessionId,
      runId: "run", timestamp: "2026-09-24T00:00:00Z", phase: "Failed", contentId: null, activityId: "file",
      parentActivityId: null, interactionId: null, name: null, text: null, details: fileDetails,
      tool: null, files: null, textTruncated: false, detailsTruncated: false, bodyOmitted: false },
  ] };
}
function toolHistory(request: HistoryRequest): HistoryResponse {
  return { ...fileHistory(request), entries: [{ ...fileHistory(request).entries[0], kind: "ToolCall", name: "literal_tool",
    details: '{"arguments":{"command":"literal input"},"result":{"content":"literal output"},"error":{"message":"supplied error"}}' }] };
}
const longBodyText = "**Supplied prose**\n\n" + "literal paragraph\n\n".repeat(110) + '<img src=x onerror="window.bodyInjected=true"> END';
function bodyHistory(request: HistoryRequest): HistoryResponse {
  return { status: "ok", next: null, tailOmitted: false, entries: [
    ["contentCompleted", "Reasoning"], ["planSnapshot", "Updated"], ["notes", "Set"],
    ["sessionUpdate", "Status"], ["error", "Failure"],
  ].map(([eventType, kind], index) => ({ ...fileHistory(request).entries[0], offset: String(index + 1), eventType, kind,
    activityId: null, contentId: String(index), text: index === 4 ? "TERSE FAILURE" : longBodyText, details: null,
    textTruncated: index === 0, bodyOmitted: index === 0 })) };
}
function navigationHistory(request: HistoryRequest): HistoryResponse {
  const mode = localStorage.getItem("navigationFixture");
  const kinds = mode === "live-only" || mode === "empty" ? [] : ["User", "CommandOutput", "Reasoning", "Unknown", "Status", "Assistant"];
  return { status: mode === "error" ? "unavailable" : "ok", tailOmitted: false,
    next: mode === "partial" && !request.cursor ? { version: 2, sessionId: request.sessionId, length: "1000",
      lastWriteUtcTicks: "7", offset: "1" } : null,
    entries: kinds.map((kind, index) => ({ offset: `${index + 1}`, eventType: kind === "Status" ? "sessionUpdate" : "contentCompleted", providerId: "fixture",
      sessionId: request.sessionId, runId: null, timestamp: "2026-09-24T00:00:00Z", kind, phase: null,
      contentId: `${index}`, activityId: null, parentActivityId: null, interactionId: null, name: null,
      text: `persisted-${kind}-${request.sessionId}` + (mode === "tabs" ? "\n\n" + Array.from({ length: 30 }, (_, n) => `Retained paragraph ${n}`).join("\n\n") : ""),
      tool: null, files: null, details: null, textTruncated: false, detailsTruncated: false, bodyOmitted: false })) };
}
Object.assign(window, { settingsShellFixture: { calls, rpcCalls, sends, choiceReads, usageReads, probes, clearRequests,
  renameRequests, projectRenames, deleteRequests, creates, snapshots, snapshotCalls, catalog, historyCalls,
  displayCalls, displayCleanup, notesCalls, lateDisplayAttempts, reminderReads, reminderSaves, reminderDetails, reminderSaveLists, projectNameReads, referenceReads,
  reminderEvidence: () => reminderTrace.map(({ signal, ...entry }) => ({ ...entry, aborted: signal?.aborted ?? null })),
  displayEvidence: () => displayAttempts.map(({ signal, ...attempt }) => ({ ...attempt, aborted: signal.aborted })),
  referenceObservations, archives, retainedQueueCalls, retainedCancelCalls, queueReceiptReads, currentReads, runtimeReads, permissionReads, permissionDecisions, permissionEntry,
  releaseCurrent(index = currentReads.length - 1, attachmentGeneration = "7") {
    const call = currentReads[index];
    // StrictMode may have detached the original waiter before its queued replacement can start.
    // Release exactly that successor too, not arbitrary future explicit refreshes.
    releaseReplacementCurrent = call.signal.aborted;
    releaseCurrentCall(call, attachmentGeneration);
  },
  inputReads, inputAnswers, inputCancels, inputEntry, fileDetails, longBodyText,
  releaseInputs() { const call = inputReads.at(-1)!; call.resolve({ status: "ok", hostEpoch: epoch,
    sessionId: call.request.sessionId, entries: [{ ...inputEntry, handle: { ...inputEntry.handle, sessionId: call.request.sessionId } }], hasMore: false }); },
  releasePermissions() { const call = permissionReads.at(-1)!; call.resolve({ status: "ok", hostEpoch: epoch,
    sessionId: call.request.sessionId, entries: [{ ...permissionEntry, handle: { ...permissionEntry.handle, sessionId: call.request.sessionId } }], hasMore: false }); },
  releaseRuntime(index = runtimeReads.length - 1, mode = "active") {
    const { request, resolve } = runtimeReads[index];
    resolve({ status: mode === "error" ? "read_failed" : "ok", hostEpoch: epoch, sessionId: request.sessionId, scope: request.scope,
      projectId: request.projectId, projectPath: request.projectPath, observation: mode === "error" ? null : {
        status: "ok", hostEpoch: epoch, sessionId: request.sessionId, runtimeInstanceId: epoch, coordinatorTransitionInProgress: mode === "transition",
        entry: mode === "absent" ? null : { attachmentGeneration: "1", isTerminated: false, isRetiring: mode === "retiring", activeRunId: "fake-run",
           queueDrainInProgress: false, providerId: "fixture", providerKey: mode === "info" ? "observed-provider" : "fixture", modelId: mode === "info" ? "observed-model" : null,
           reasoningEffort: mode === "info" ? "High" : null, agentPromptId: mode === "info" ? "current-agent" : null, pendingAgentPromptId: mode === "info" ? "pending-agent" : null,
           activity: { timestamp: "2026-01-01T12:00:00.0000001+02:00", source: "admitted_agent_event", admittedEvents: "2", omittedEvents: "1" } } } });
  },
  releaseInfoUsage(attachment = "1") {
    const read = usageReads.at(-1)!;
    const request = read.request as { expectedHostEpoch: string; sessionId: string };
    read.resolve({ status: "ok", hostEpoch: request.expectedHostEpoch, sessionId: request.sessionId, runtimeInstanceId: epoch,
      attachmentGeneration: attachment, omittedUsageEvents: "2", observation: { sequence: "1", scope: "CurrentWindow", source: "LocalProviderUsage",
        sourceUpdatedAt: null, eventTimestamp: "2026-01-01T00:00:00Z", hadInvalidValues: true, hadOmittedData: true,
        window: { currentTokens: "0", tokenLimit: null, messageCount: 0 }, lastOperation: null } });
  },
  releaseArchive(status = "ok") {
    const work = archives.at(-1)!; const request = work.request;
    if (status === "ok") catalog.projects.find(project => project.id === request.projectId)!.archived = request.archived;
    work.resolve({ status, hostEpoch: epoch, projectId: request.projectId, projectPath: request.projectPath,
      sourcePath: request.sourcePath, revision: request.revision, archived: request.archived });
  },
  releaseReferenceObservation(index = referenceObservations.length - 1, items?: unknown[]) {
    const read = referenceObservations[index];
    read.resolve({ status: "ok", epoch, omitted: false, items: items ?? [{ start: 0, length: read.request.text.trimEnd().length, status: "resolved" }] });
  },
  releaseReferences(index = referenceReads.length - 1) { const read = referenceReads[index]; read.resolve({ status: "ok", epoch, omitted: false,
    items: [{ path: "src/app.cs", directory: false, recent: true }, { path: "Settings/Copy", directory: true, recent: false }] }); },
  releaseProjectName() { const read = projectNameReads.shift()!; read.resolve({ status: "ok", hostEpoch: epoch,
    ...read.request, displayName: "Original name", sourcePath: "/fixture/projects.yml", revision: "A".repeat(64) }); },
  releaseReminder(index: number) {
    const read = reminderReads[index];
    read.resolve({ status: "ok", epoch: read.request.expectedEpoch, sessionId: read.request.sessionId,
      reminders: [], activeCount: 0, completedCount: 0 });
  },
  failSend() { sendFailures.shift()?.(); },
  releaseHistory() { for (const release of historyReads.splice(0)) release(); },
  releaseCreate(status = "ok", providerId?: string) { const original = creates[0]; original.resolve({ status, hostEpoch: epoch,
    scope: original.request.scope, projectId: original.request.projectId, projectPath: original.request.projectPath,
    sessionId: "created", workspacePath: original.request.projectPath ?? "/fixture/global", providerId: providerId ?? original.request.providerId }); },
  releaseSnapshot(mode = "ok") { const read = snapshots.shift()!;
    if (mode === "error") read.reject(new Error("fixture read failed"));
    else read.resolve({ ...catalog, sessions: [...catalog.sessions, ...(mode === "missing" ? [] :
      [{ ...session, id: "created", title: "created", fullTitle: "created",
        ...(creates[0]?.request.scope === "global" ? { scopeKind: "global", projectId: null, workspacePath: "/fixture/global" } : {}),
        providerKey: mode === "provider" ? "wrong-provider" : creates[0]?.request.providerId ?? session.providerKey }])] }); },
  releaseMutation(kind: "rename" | "delete") { const index = mutationReplies.findIndex(reply => reply.kind === kind);
    if (index >= 0) mutationReplies.splice(index, 1)[0].resolve({}); },
  releaseExactDelete(status = "ok", hostEpoch?: string) { const index = mutationReplies.findIndex(reply => reply.kind === "delete");
    const request = deleteRequests.at(-1) as { expectedHostEpoch: string };
    if (index >= 0) mutationReplies.splice(index, 1)[0].resolve({ ...request, hostEpoch: hostEpoch ?? request.expectedHostEpoch, status }); },
  releaseChoices(mode: "ok" | "stale" | "different" = "ok") { for (const read of choiceReads.splice(0)) {
    const value = choices(read.request);
    read.resolve(mode === "stale" ? { ...value, status: "stale_epoch", epoch: "different-host" }
      : mode === "different" ? { ...value, models: value.models.filter(model => model.id === "old") } : value);
  } } } });
const owned = () => localStorage.getItem("settingsFixtureOwned") === "true";
const choices = (request: { expectedEpoch: string; sessionId: string }) => ({ status: localStorage.getItem("chooserFixtureDisabled") === "true" ? "disabled" : "ok", epoch: request.expectedEpoch, sessionId: request.sessionId,
  current: { providerKey: "fixture", agentPromptId: "default", modelId: "old", reasoningEffort: "Low" },
  prompts: [{ id: "default", name: "Default" }, { id: "plan", name: "Plan" }],
  models: [{ id: "old", name: "Old", efforts: ["Low"], imageInput: false }, ...(localStorage.getItem("settingsFixtureNewChoices") === "true"
    ? [{ id: "new", name: "New", efforts: ["High"], imageInput: true }] : [])] });
export const boot = { status: async () => ({ state: owned() ? "owned" : "catalog", hostAvailable: owned(),
  hostEpoch: owned() ? epoch : null, commandReviewEnabled: localStorage.getItem("permissionFixtureEnabled") === "true",
  ownedUserInputEnabled: localStorage.getItem("inputFixtureEnabled") === "true", productName: "CodeAlta", version: "development" }) };
export const workspace = { snapshot: async () => {
  snapshotCalls.push({});
  if (localStorage.getItem("creationFixtureHoldSnapshot") === "true")
    return new Promise((resolve, reject) => snapshots.push({ resolve, reject }));
  if (localStorage.getItem("settingsFixtureWorkspaceError") === "true") throw new Error("fixture catalog unavailable");
  return localStorage.getItem("settingsFixtureFreshSnapshot") === "true"
    ? { ...catalog, sessions: catalog.sessions.map(row => ({ ...row, updatedAt: "2026-09-26T00:00:00Z" })) } : catalog;
},
  draftPrompts: async (request: { expectedHostEpoch: string; projectId: string | null; projectPath: string | null }) => ({
    status: "ok", hostEpoch: request.expectedHostEpoch, projectId: request.projectId, projectPath: request.projectPath,
    prompts: [{ id: "default", name: "Default" }, { id: "plan", name: "Plan" }],
  }),
  historyTail: (request: HistoryRequest) => {
    if (localStorage.getItem("bodyFixtureEnabled") === "true") { historyCalls.push(request); return Promise.resolve(bodyHistory(request)); }
    if (localStorage.getItem("toolFixtureEnabled") === "true") { historyCalls.push(request); return Promise.resolve(toolHistory(request)); }
    if (localStorage.getItem("fileFixtureEnabled") === "true") { historyCalls.push(request); return Promise.resolve(fileHistory(request)); }
    if (!localStorage.getItem("navigationFixture")) return unavailable();
    historyCalls.push(request);
    if (localStorage.getItem("navigationFixture") === "loading" ||
      (localStorage.getItem("navigationFixture") === "partial" && request.cursor))
      return new Promise<HistoryResponse>(resolve => historyReads.push(() => resolve(navigationHistory(request))));
    return Promise.resolve(navigationHistory(request));
  },
  historyTimeline: async (request: HistoryRequest): Promise<TimelineHistoryResponse> => ({
    page: await workspace.historyTail(request), revision: null, sources: [],
  }),
  // Existing fixtures supply projected events, not physical journal bytes. Never invent full source.
  historySource: async (): Promise<HistorySourceResponse> => ({ status: "unconfigured", text: null, nextOffset: null }),
  openProject: unavailable, readProjectName: (request: { projectId: string; projectPath: string }) =>
    localStorage.getItem("draftTabFixture") === "true" ? new Promise(resolve => projectNameReads.push({ request, resolve })) : unavailable(),
  archiveProject: (request: WorkspaceArchiveProjectRequest) => request.confirmed ? new Promise(resolve => archives.push({ request, resolve }))
    : Promise.resolve({ status: "confirmation_required", hostEpoch: epoch, projectId: request.projectId, projectPath: request.projectPath,
      sourcePath: "/fixture/catalog/project.md", revision: "A".repeat(64), archived: request.expectedArchived }),
  renameProject: (request: { displayName: string }) => new Promise((resolve, reject) => projectRenames.push({ request, resolve, reject })), createSession: (request: CreateRequest) => new Promise(resolve => creates.push({ request, resolve })),
  renameSession: (request: unknown) => { renameRequests.push(request); return new Promise(resolve => mutationReplies.push({ kind: "rename", resolve })); },
  deleteSession: (request: unknown) => { deleteRequests.push(request); return new Promise(resolve => mutationReplies.push({ kind: "delete", resolve })); } };
export const configuration = { snapshot: async () => ({ providers: [
  { id: "fixture", name: "Fixture", enabled: true }, { id: "alternate", name: "Alternate", enabled: true },
  { id: "disabled", name: "Disabled", enabled: false }], providerRuntimeAvailable: owned(), plugins: [], pluginRuntimeAvailable: false }) };
export const applicationLogs = { read: async () => { calls.push("logs"); return localStorage.getItem("settingsFixtureLogsOk") === "true"
  ? { status: "ok", rows: [{ timestamp: "t", level: "Info", logger: "fixture", text: "fixture row", textTruncated: false }],
    captureOmitted: "0", readOmitted: 0, captureId: "11111111-1111-4111-8111-111111111111", boundary: "1",
    grant: "22222222-2222-4222-8222-222222222222" }
  : { status: "unavailable", rows: [], captureOmitted: "0", readOmitted: 0, captureId: null, boundary: "0", grant: "" }; },
  clear: (request: unknown) => { clearRequests.push(request); return new Promise(() => {}); } };
export const modelCatalog = { providers: async () => ({ status: "ok", epoch, truncated: false,
  providers: [{ id: "fixture", name: "Fixture", type: "test", isDefault: true, defaultModel: "old", observedAt: null,
    availability: "Unknown", enabled: true }] }),
models: async () => ({ status: "ok", epoch, providerId: "fixture", availability: "Ready", truncated: false,
  models: [{ id: "new", name: "New model", description: null, efforts: ["High"], defaultEffort: "High", contextTokens: null,
    inputTokens: null, outputTokens: null, reasoning: true, tools: null, structuredOutput: null, imageInput: null },
    { id: "old", name: "Old model", description: null, efforts: [], defaultEffort: null, contextTokens: null,
      inputTokens: null, outputTokens: null, reasoning: false, tools: null, structuredOutput: null, imageInput: null }] }),
  probe: (request: unknown) => { probes.push(request); return new Promise(() => {}); } };
export const reminder = { list: (request: ReminderListRequest, options: { signal: AbortSignal }) =>
  localStorage.getItem("layoutFixtureSave") === "true"
    ? (reminderSaveLists.push(request), traceReminder("list", request, options.signal).completed = true, Promise.resolve({ status: "ok", epoch: request.expectedEpoch, sessionId: request.sessionId,
      reminders: [{ id: "reminder-1", state: "active", preview: "original", delaySeconds: 60, repeatCount: 1, firedCount: 0, dueAt: null, lastExitCode: null, lastError: null }], activeCount: 1, completedCount: 0 }))
    : localStorage.getItem("layoutFixtureReminders") === "true"
    ? new Promise<ReminderListResponse>(resolve => reminderReads.push({ request, signal: options.signal, resolve }))
    : unavailable(), detail: (request: import("#neoastra").ReminderDetailRequest, options: { signal: AbortSignal }) => {
      reminderDetails.push(request);
      traceReminder("detail", request, options.signal).completed = true;
      return localStorage.getItem("layoutFixtureSave") === "true" ? Promise.resolve({ status: "ok", epoch: request.expectedEpoch,
        sessionId: request.sessionId, reminderId: request.reminderId, content: "original", delaySeconds: 60, repeatCount: 1, editRevision: "0" }) : unavailable();
    }, create: unavailable, delete: unavailable,
    save: (request: import("#neoastra").ReminderSaveRequest) => {
      const entry = traceReminder("save", request);
      return new Promise<import("#neoastra").ReminderMutationResponse>((resolve, reject) => reminderSaves.push({ request,
        resolve: value => { entry.completed = true; resolve(value); }, reject: error => { entry.completed = true; reject(error); } }));
    } };
export const sessionDisplay = { observe: async (request: SessionDisplayRequest, options: { signal: AbortSignal }) => {
  displayCalls.push(request);
  const attempt = { sessionId: request.sessionId, signal: options.signal, opened: false, settled: false, unavailable: false };
  displayAttempts.push(attempt);
  if (!localStorage.getItem("navigationFixture")) {
    attempt.unavailable = true;
    try { return await unavailable(); } finally { attempt.settled = true; }
  }
  const hold = localStorage.getItem("layoutFixtureLive") === "true";
  const item: SessionDisplayItem = { status: "ok", hostEpoch: request.expectedHostEpoch, sessionId: request.sessionId,
    projectionEpoch: "navigation-fixture", revision: "0", previousRevision: null, isInitial: true, hasGap: false,
    isClosed: !hold, isPartial: true, evictedSessions: "0", omittedSessionEvents: "0",
    session: { sessionId: request.sessionId, revision: "0", lifecycle: null, queuedPromptCount: null, configuration: null,
      statusKind: null, statusMessage: null, metadataTruncated: false, transportTruncated: false, evictedTextItems: "0",
      unsupportedEvents: "0", toolActivities: [], evictedToolActivities: "0",
      text: localStorage.getItem("navigationFixture") === "empty" ? [] : ["User", "Assistant", "Unknown"].map(kind => ({
        timestamp: null, sequence: null, runId: "live-run", contentId: kind, kind, text: `live-${kind}`, isComplete: true, isTruncated: false, startedWithDelta: false })) } };
  return (async function* () {
    attempt.opened = true;
    try {
      yield item;
      if (hold && !options.signal.aborted) await new Promise<void>(resolve => {
        options.signal.addEventListener("abort", () => resolve(), { once: true });
      });
      if (hold && options.signal.aborted && localStorage.getItem("layoutFixtureReminders") === "true") {
        // Deliberately late fake-provider delivery: cancellation must not publish
        // into the App-owned display cache or a subsequent workspace attachment.
        lateDisplayAttempts.push(request.sessionId);
        yield { ...item, revision: "1", previousRevision: "0", isInitial: false,
          session: { ...item.session!, revision: "1", text: [{ runId: "late", contentId: "late", kind: "User",
            text: "stale-disposed-display", isComplete: true, isTruncated: false, startedWithDelta: false }] } };
      }
    } finally { attempt.settled = true; displayCleanup.push(request.sessionId); }
  })();
} };
export const sessionRuntimeState = { current: (request: SessionRuntimeStateRequest, options: { signal: AbortSignal; timeoutMilliseconds: number }) =>
  localStorage.getItem("retainedQueueFixture") === "true"
    ? new Promise<SessionRuntimeStateResponse>((resolve, reject) => {
      const call = { request, signal: options.signal, resolve, reject };
      currentReads.push(call);
      if (releaseReplacementCurrent) { releaseReplacementCurrent = false; releaseCurrentCall(call); }
    }) : unavailable(),
  observe: (request: SessionRuntimeScopedRequest, options: { signal: AbortSignal }) =>
  new Promise((resolve, reject) => runtimeReads.push({ request, signal: options.signal, resolve, reject })) };
export const sessionUsage = { read: (request: unknown) => new Promise((resolve, reject) => usageReads.push({ request, resolve, reject })) };
export const sessionPermissions = {
  list: (request: SessionPermissionsRequest) => new Promise(resolve => permissionReads.push({ request, resolve })),
  resolve: (request: SessionPermissionResolveRequest) => new Promise((resolve, reject) => permissionDecisions.push({ request, resolve, reject })),
};
export const sessionOperations = { observeReferences: (request: { text: string }) => new Promise(resolve => referenceObservations.push({ request, resolve })),
  ...(localStorage.getItem("retainedQueueFixture") === "true" ? {
    receipts: (request: SessionReceiptRequest) => new Promise<SessionReceiptPage>(resolve => queueReceiptReads.push({ request, resolve })),
  } : {}),
  searchReferences: (request: unknown) => new Promise(resolve => referenceReads.push({ request, resolve })),
  choices: (request: { expectedEpoch: string; sessionId: string }) =>
  localStorage.getItem("settingsFixtureHoldChoices") === "true" ? new Promise(resolve => choiceReads.push({ request, resolve })) : Promise.resolve(choices(request)),
  send: (request: unknown) => { sends.push(request); return new Promise((_resolve, reject) => {
    sendFailures.push(() => reject(new Error("fixture transport uncertainty")));
  }); }, abort: unavailable, steer: unavailable,
  compact: unavailable, abortRun: unavailable,
  queue: (request: unknown) => localStorage.getItem("retainedQueueFixture") === "true"
    ? new Promise((resolve, reject) => retainedQueueCalls.push({ request, resolve, reject })) : unavailable(),
  cancelQueue: (request: SessionCancelQueueRequest) => localStorage.getItem("retainedQueueFixture") === "true"
    ? new Promise<SessionAdmission>((resolve, reject) => retainedCancelCalls.push({ request, resolve, reject })) : unavailable() };
export const sessionAsks = { answer: unavailable, cancel: unavailable };
export const sessionNotes = { current: (request: unknown) => { notesCalls.push(request); return unavailable(); }, clear: unavailable };
// Outside the observed set below: plugin cards are an optional decoration of a settled timeline.
export const sessionPluginEvents = { read: unavailable };
export const sessionUserInput = {
  list: (request: { expectedHostEpoch: string; sessionId: string }) => new Promise(resolve => inputReads.push({ request, resolve })),
  resolve: (request: InputRequest) => new Promise((resolve, reject) => inputAnswers.push({ request, resolve, reject })),
  cancel: (request: Omit<InputRequest, "answers">) => new Promise((resolve, reject) => inputCancels.push({ request, resolve, reject })),
};

// Observe every fake bridge invocation; language changes must not start backend work.
for (const [name, service] of Object.entries({ boot, workspace, configuration, applicationLogs, modelCatalog,
  reminder, sessionDisplay, sessionRuntimeState, sessionUsage,
  sessionPermissions, sessionOperations, sessionAsks, sessionNotes, sessionUserInput })) {
  for (const [method, invoke] of Object.entries(service)) {
    Object.defineProperty(service, method, { value: (...args: unknown[]) => {
      rpcCalls.push(`${name}.${method}`);
      return (invoke as (...values: unknown[]) => unknown)(...args);
    } });
  }
}
