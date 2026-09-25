// Isolated bridge for mounting the actual main.tsx in a local browser test.
// No native bridge or user data is touched.
const epoch = "12345678-1234-1234-1234-123456789abc";
const session = { id: "one", title: "one", fullTitle: "one", fullTitleTruncated: false,
  parentSessionId: null, scopeKind: "project", projectId: "project", lineageIssue: null,
  workspacePath: "/fixture/project", providerKey: "fixture", updatedAt: "2026-09-24T00:00:00Z" };
const catalog = { configured: true, projects: [{ id: "project", name: "Project", path: "/fixture/project", archived: localStorage.getItem("usageFixtureArchived") === "true" }],
  sessions: [session, { ...session, id: "two", title: "two", fullTitle: "two" }],
  projectsTruncated: localStorage.getItem("usageFixtureTruncated") === "true", sessionsTruncated: false, displayTextTruncated: false };
const unavailable = async () => { throw new Error("test bridge unavailable"); };
const calls: string[] = [];
const sends: unknown[] = [];
const choiceReads: Array<{ request: { expectedEpoch: string; sessionId: string }; resolve: (value: unknown) => void }> = [];
const usageReads: Array<{ request: unknown; resolve: (value: unknown) => void; reject: (error: Error) => void }> = [];
const probes: unknown[] = [];
const clearRequests: unknown[] = [];
Object.assign(window, { settingsShellFixture: { calls, sends, choiceReads, usageReads, probes, clearRequests,
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
export const workspace = { snapshot: async () => catalog, openProject: unavailable, readProjectName: unavailable,
  renameProject: unavailable, createSession: unavailable, renameSession: unavailable, deleteSession: unavailable };
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
export const sessionDisplay = { observe: unavailable };
export const sessionRuntimeState = { current: unavailable };
export const sessionUsage = { read: (request: unknown) => new Promise((resolve, reject) => usageReads.push({ request, resolve, reject })) };
export const sessionPermissions = { list: unavailable, resolve: unavailable };
export const sessionOperations = { choices: (request: { expectedEpoch: string; sessionId: string }) =>
  localStorage.getItem("settingsFixtureHoldChoices") === "true" ? new Promise(resolve => choiceReads.push({ request, resolve })) : Promise.resolve(choices(request)),
  send: (request: unknown) => { sends.push(request); return new Promise(() => {}); }, abort: unavailable, steer: unavailable,
  compact: unavailable, abortRun: unavailable, queue: unavailable, cancelQueue: unavailable };
export const sessionAsks = { answer: unavailable, cancel: unavailable };
export const sessionNotes = { current: unavailable, clear: unavailable };
export const sessionUserInput = { list: unavailable, resolve: unavailable, cancel: unavailable };
