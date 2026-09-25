// Isolated bridge for mounting the actual main.tsx in a local browser test.
// No native bridge or user data is touched.
const epoch = "12345678-1234-1234-1234-123456789abc";
const session = { id: "one", title: "one", fullTitle: "one", fullTitleTruncated: false,
  parentSessionId: null, scopeKind: "project", projectId: "project", lineageIssue: null,
  workspacePath: "/fixture/project", providerKey: "fixture", updatedAt: "2026-09-24T00:00:00Z" };
const catalog = { configured: true, projects: [{ id: "project", name: "Project", path: "/fixture/project", archived: false }],
  sessions: [session], projectsTruncated: false, sessionsTruncated: false, displayTextTruncated: false };
const unavailable = async () => { throw new Error("test bridge unavailable"); };
const calls: string[] = [];
Object.assign(window, { settingsShellFixture: { calls } });
export const boot = { status: async () => ({ state: "catalog", hostAvailable: false, hostEpoch: null, productName: "CodeAlta", version: "development" }) };
export const workspace = { snapshot: async () => catalog, openProject: unavailable, readProjectName: unavailable,
  renameProject: unavailable, createSession: unavailable, renameSession: unavailable, deleteSession: unavailable };
export const configuration = { snapshot: async () => ({ providers: [], plugins: [], pluginRuntimeAvailable: false }) };
export const applicationLogs = { read: async () => { calls.push("logs"); return { status: "unavailable", rows: [], captureOmitted: "0", readOmitted: 0,
  captureId: null, boundary: "0", grant: "" }; }, clear: unavailable };
export const modelCatalog = { providers: unavailable, models: unavailable, probe: unavailable };
export const promptCatalog = { list: unavailable };
export const mcpInventory = { list: unavailable };
export const reminder = { list: unavailable, detail: unavailable, create: unavailable, delete: unavailable, save: unavailable };
export const sessionDisplay = { observe: unavailable };
export const sessionRuntimeState = { current: unavailable };
export const sessionPermissions = { list: unavailable, resolve: unavailable };
export const sessionOperations = { choices: unavailable, send: unavailable, abort: unavailable, steer: unavailable,
  compact: unavailable, abortRun: unavailable, queue: unavailable, cancelQueue: unavailable };
export const sessionAsks = { answer: unavailable, cancel: unavailable };
export const sessionNotes = { current: unavailable, clear: unavailable };
export const sessionUserInput = { list: unavailable, resolve: unavailable, cancel: unavailable };
