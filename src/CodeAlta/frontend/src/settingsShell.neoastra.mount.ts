// Isolated bridge for mounting the actual main.tsx in a local browser test.
// No native bridge or user data is touched.
import type { HistoryRequest, HistoryResponse, SessionDisplayItem, SessionDisplayRequest } from "#neoastra";
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
const unavailable = async () => { throw new Error("test bridge unavailable"); };
const calls: string[] = [];
const sends: unknown[] = [];
const sendFailures: Array<() => void> = [];
const displayCalls: SessionDisplayRequest[] = [];
const displayCleanup: string[] = [];
const notesCalls: unknown[] = [];
const choiceReads: Array<{ request: { expectedEpoch: string; sessionId: string }; resolve: (value: unknown) => void }> = [];
const usageReads: Array<{ request: unknown; resolve: (value: unknown) => void; reject: (error: Error) => void }> = [];
const probes: unknown[] = [];
const clearRequests: unknown[] = [];
const renameRequests: unknown[] = [];
const deleteRequests: unknown[] = [];
const mutationReplies: Array<{ kind: "rename" | "delete"; resolve: (value: unknown) => void }> = [];
type CreateRequest = { expectedHostEpoch: string; scope: string; projectId: string | null; projectPath: string | null; title: string | null };
const creates: Array<{ request: CreateRequest; resolve: (value: unknown) => void }> = [];
const snapshots: Array<{ resolve: (value: unknown) => void; reject: (error: Error) => void }> = [];
const snapshotCalls: unknown[] = [];
const historyCalls: HistoryRequest[] = [];
const historyReads: Array<() => void> = [];
function navigationHistory(request: HistoryRequest): HistoryResponse {
  const mode = localStorage.getItem("navigationFixture");
  const kinds = mode === "live-only" || mode === "empty" ? [] : ["User", "CommandOutput", "Reasoning", "Unknown", "Status", "Assistant"];
  return { status: mode === "error" ? "unavailable" : "ok", tailOmitted: false,
    next: mode === "partial" && !request.cursor ? { version: 2, sessionId: request.sessionId, length: "1000",
      lastWriteUtcTicks: "7", offset: "1" } : null,
    entries: kinds.map((kind, index) => ({ offset: `${index + 1}`, eventType: kind === "Status" ? "sessionUpdate" : "contentCompleted", providerId: "fixture",
      sessionId: request.sessionId, runId: null, timestamp: "2026-09-24T00:00:00Z", kind, phase: null,
      contentId: `${index}`, activityId: null, parentActivityId: null, interactionId: null, name: null,
      text: `persisted-${kind}-${request.sessionId}`, details: null, textTruncated: false, detailsTruncated: false, bodyOmitted: false })) };
}
Object.assign(window, { settingsShellFixture: { calls, sends, choiceReads, usageReads, probes, clearRequests,
  renameRequests, deleteRequests, creates, snapshots, snapshotCalls, catalog, historyCalls,
  displayCalls, displayCleanup, notesCalls,
  failSend() { sendFailures.shift()?.(); },
  releaseHistory() { for (const release of historyReads.splice(0)) release(); },
  releaseCreate(status = "ok") { const original = creates[0]; original.resolve({ status, hostEpoch: epoch,
    scope: original.request.scope, projectId: original.request.projectId, projectPath: original.request.projectPath,
    sessionId: "created", workspacePath: original.request.projectPath ?? "/fixture/global" }); },
  releaseSnapshot(mode = "ok") { const read = snapshots.shift()!;
    if (mode === "error") read.reject(new Error("fixture read failed"));
    else read.resolve({ ...catalog, sessions: [...catalog.sessions, ...(mode === "missing" ? [] :
      [{ ...session, id: "created", title: "created", fullTitle: "created" }])] }); },
  releaseMutation(kind: "rename" | "delete") { const index = mutationReplies.findIndex(reply => reply.kind === kind);
    if (index >= 0) mutationReplies.splice(index, 1)[0].resolve({}); },
  releaseChoices(mode: "ok" | "stale" | "different" = "ok") { for (const read of choiceReads.splice(0)) {
    const value = choices(read.request);
    read.resolve(mode === "stale" ? { ...value, status: "stale_epoch", epoch: "different-host" }
      : mode === "different" ? { ...value, models: value.models.filter(model => model.id === "old") } : value);
  } } } });
const owned = () => localStorage.getItem("settingsFixtureOwned") === "true";
const choices = (request: { expectedEpoch: string; sessionId: string }) => ({ status: "ok", epoch: request.expectedEpoch, sessionId: request.sessionId,
  current: { providerKey: "fixture", agentPromptId: "default", modelId: "old", reasoningEffort: "Low" },
  prompts: [{ id: "default", name: "Default" }, { id: "plan", name: "Plan" }],
  models: [{ id: "old", name: "Old", efforts: ["Low"] }, ...(localStorage.getItem("settingsFixtureNewChoices") === "true"
    ? [{ id: "new", name: "New", efforts: ["High"] }] : [])] });
export const boot = { status: async () => ({ state: owned() ? "owned" : "catalog", hostAvailable: owned(),
  hostEpoch: owned() ? epoch : null, productName: "CodeAlta", version: "development" }) };
export const workspace = { snapshot: async () => {
  snapshotCalls.push({});
  if (localStorage.getItem("creationFixtureHoldSnapshot") === "true")
    return new Promise((resolve, reject) => snapshots.push({ resolve, reject }));
  if (localStorage.getItem("settingsFixtureWorkspaceError") === "true") throw new Error("fixture catalog unavailable");
  return localStorage.getItem("settingsFixtureFreshSnapshot") === "true"
    ? { ...catalog, sessions: catalog.sessions.map(row => ({ ...row, updatedAt: "2026-09-26T00:00:00Z" })) } : catalog;
},
  historyTail: (request: HistoryRequest) => {
    if (!localStorage.getItem("navigationFixture")) return unavailable();
    historyCalls.push(request);
    if (localStorage.getItem("navigationFixture") === "loading" ||
      (localStorage.getItem("navigationFixture") === "partial" && request.cursor))
      return new Promise<HistoryResponse>(resolve => historyReads.push(() => resolve(navigationHistory(request))));
    return Promise.resolve(navigationHistory(request));
  },
  openProject: unavailable, readProjectName: unavailable,
  renameProject: unavailable, createSession: (request: CreateRequest) => new Promise(resolve => creates.push({ request, resolve })),
  renameSession: (request: unknown) => { renameRequests.push(request); return new Promise(resolve => mutationReplies.push({ kind: "rename", resolve })); },
  deleteSession: (request: unknown) => { deleteRequests.push(request); return new Promise(resolve => mutationReplies.push({ kind: "delete", resolve })); } };
export const configuration = { snapshot: async () => ({ providers: [], plugins: [], pluginRuntimeAvailable: false }) };
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
export const promptCatalog = { list: async () => ({ status: "ok", epoch, sessionId: "one", truncated: false,
  prompts: [{ id: "plan", name: "Plan", description: null, builtIn: true, appended: false, bodyTruncated: false, body: "Plan", scope: "BuiltIn" }] }) };
export const mcpInventory = { list: unavailable };
export const reminder = { list: unavailable, detail: unavailable, create: unavailable, delete: unavailable, save: unavailable };
export const sessionDisplay = { observe: async (request: SessionDisplayRequest, options: { signal: AbortSignal }) => {
  displayCalls.push(request);
  if (!localStorage.getItem("navigationFixture")) return unavailable();
  const hold = localStorage.getItem("layoutFixtureLive") === "true";
  const item: SessionDisplayItem = { status: "ok", hostEpoch: request.expectedHostEpoch, sessionId: request.sessionId,
    projectionEpoch: "navigation-fixture", revision: "0", previousRevision: null, isInitial: true, hasGap: false,
    isClosed: !hold, isPartial: true, evictedSessions: "0", omittedSessionEvents: "0",
    session: { sessionId: request.sessionId, revision: "0", lifecycle: null, queuedPromptCount: null, configuration: null,
      statusKind: null, statusMessage: null, metadataTruncated: false, transportTruncated: false, evictedTextItems: "0",
      unsupportedEvents: "0", toolActivities: [], evictedToolActivities: "0",
      text: localStorage.getItem("navigationFixture") === "empty" ? [] : ["User", "Assistant", "Unknown"].map(kind => ({
        runId: "live-run", contentId: kind, kind, text: `live-${kind}`, isComplete: true, isTruncated: false, startedWithDelta: false })) } };
  return (async function* () {
    try {
      yield item;
      if (hold && !options.signal.aborted) await new Promise<void>(resolve => {
        options.signal.addEventListener("abort", () => resolve(), { once: true });
      });
    } finally { displayCleanup.push(request.sessionId); }
  })();
} };
export const sessionRuntimeState = { current: unavailable };
export const sessionUsage = { read: (request: unknown) => new Promise((resolve, reject) => usageReads.push({ request, resolve, reject })) };
export const sessionPermissions = { list: unavailable, resolve: unavailable };
export const sessionOperations = { choices: (request: { expectedEpoch: string; sessionId: string }) =>
  localStorage.getItem("settingsFixtureHoldChoices") === "true" ? new Promise(resolve => choiceReads.push({ request, resolve })) : Promise.resolve(choices(request)),
  send: (request: unknown) => { sends.push(request); return new Promise((_resolve, reject) => {
    sendFailures.push(() => reject(new Error("fixture transport uncertainty")));
  }); }, abort: unavailable, steer: unavailable,
  compact: unavailable, abortRun: unavailable, queue: unavailable, cancelQueue: unavailable };
export const sessionAsks = { answer: unavailable, cancel: unavailable };
export const sessionNotes = { current: (request: unknown) => { notesCalls.push(request); return unavailable(); }, clear: unavailable };
export const sessionUserInput = { list: unavailable, resolve: unavailable, cancel: unavailable };
