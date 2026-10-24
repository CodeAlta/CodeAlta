/* Local Vite-only demo backend. The packaged desktop always resolves #neoastra to the generated bridge. */
import type {
  BootStatus,
  ColorSchemeColors,
  ColorSchemeDocument,
  ColorSchemeSaveRequest,
  ConfigurationSnapshot,
  HistoryRequest,
  HistoryResponse,
  SessionClaudeCodeRequest,
  WorkspaceClaudeCodeSessionRequest, WorkspaceClaudeCodeSessionPreviewRequest,
  WorkspaceClaudeCodeConversationsRequest,
  WorkspaceClaudeCodeSessionsRequest,
  WorkspaceForkSessionRequest,
  WorkspaceImportClaudeCodeSessionRequest,
  WorkspaceRewindSessionRequest,
  WorkspaceSessionTurnsRequest,
  WorkspaceSnapshot,
} from "../../obj/neoastra/neoastra";

export type * from "../../obj/neoastra/neoastra";

const snapshot: WorkspaceSnapshot = {
  configured: true,
  displayTextTruncated: false,
  projectsTruncated: false,
  sessionsTruncated: false,
  projects: [
    { id: "codealta", name: "CodeAlta", path: "C:\\code\\CodeAlta", archived: false },
    { id: "docs", name: "Documentation", path: "C:\\work\\docs", archived: false },
  ],
  sessions: [
    { messageCount: null, automationId: null, worktreePath: null, worktreeRoot: null, worktreeName: null, worktreeMissing: false, forkedFromSessionId: null, forkedFromTitle: null, forkKind: null, forkTurnCount: 0, rewindCount: 0, archived: false, archivedAt: null, spaceId: null, createdAt: null, id: "demo-active", title: "Build the desktop workspace", fullTitle: "Build the desktop workspace", fullTitleTruncated: false, parentSessionId: null, scopeKind: "project", projectId: "codealta", lineageIssue: null, workspacePath: "C:\\code\\CodeAlta", providerKey: "openai", updatedAt: "A few seconds ago" },
    { messageCount: null, automationId: null, worktreePath: null, worktreeRoot: null, worktreeName: null, worktreeMissing: false, forkedFromSessionId: null, forkedFromTitle: null, forkKind: null, forkTurnCount: 0, rewindCount: 0, archived: false, archivedAt: null, spaceId: null, createdAt: null, id: "demo-review", title: "Review runtime ownership", fullTitle: "Review runtime ownership", fullTitleTruncated: false, parentSessionId: null, scopeKind: "project", projectId: "codealta", lineageIssue: null, workspacePath: "C:\\code\\CodeAlta", providerKey: "openai", updatedAt: "18 minutes ago" },
    { messageCount: null, automationId: null, worktreePath: null, worktreeRoot: null, worktreeName: null, worktreeMissing: false, forkedFromSessionId: null, forkedFromTitle: null, forkKind: null, forkTurnCount: 0, rewindCount: 0, archived: false, archivedAt: null, spaceId: null, createdAt: null, id: "demo-docs", title: "Improve onboarding", fullTitle: "Improve onboarding", fullTitleTruncated: false, parentSessionId: null, scopeKind: "project", projectId: "docs", lineageIssue: null, workspacePath: "C:\\work\\docs", providerKey: "anthropic", updatedAt: "Yesterday" },
    { messageCount: null, automationId: null, worktreePath: null, worktreeRoot: null, worktreeName: null, worktreeMissing: false, forkedFromSessionId: null, forkedFromTitle: null, forkKind: null, forkTurnCount: 0, rewindCount: 0, archived: false, archivedAt: null, spaceId: null, createdAt: null, id: "demo-global", title: "Explore CodeAlta", fullTitle: "Explore CodeAlta", fullTitleTruncated: false, parentSessionId: null, scopeKind: "global", projectId: null, lineageIssue: null, workspacePath: null, providerKey: null, updatedAt: "Last week" },
  ],
};

const historyEntries: HistoryResponse["entries"] = [
  {
    tool: null, files: null, images: null, activityId: null, bodyOmitted: false, contentId: "user-1", eventType: "contentCompleted", kind: "User",
    details: null, detailsTruncated: false, interactionId: null, sourceSessionId: null, prompt: null, name: null, offset: "0", parentActivityId: null, phase: null, providerId: "demo", runId: "demo-run",
    sessionId: "demo-active", text: "Create a usable desktop workspace I can run locally.", textTruncated: false,
    timestamp: "2026-09-22T10:12:00Z",
  },
  {
    tool: null, files: null, images: null, activityId: null, bodyOmitted: false, contentId: "assistant-1", eventType: "contentCompleted", kind: "Assistant",
    details: null, detailsTruncated: false, interactionId: null, sourceSessionId: null, prompt: null, name: null, offset: "1", parentActivityId: null, phase: null, providerId: "demo", runId: "demo-run",
    sessionId: "demo-active", text: "The workspace shell is ready. Select sessions, inspect history, and try the composer below.", textTruncated: false,
    timestamp: "2026-09-22T10:12:04Z",
  },
];

const bootStatus: BootStatus = {
  commandReviewEnabled: false,
  hostAvailable: false,
  hostEpoch: null,
  ownedAsksEnabled: false,
  ownedUserInputEnabled: false,
  developerMode: false,
  configRecovery: false,
  providerSetup: false,
  closing: null,
  productName: "CodeAlta Desktop Demo",
  state: "demo",
  version: "local preview",
};

export const neoRpcContractHash = "demo";
export const boot = Object.freeze({ status: async () => bootStatus, appearance: async () => ({ status: "ok" }), report: async () => ({ status: "ok" }) });
export const configuration = Object.freeze({ snapshot: async (): Promise<ConfigurationSnapshot> => ({
  providerRuntimeAvailable: true,
  pluginRuntimeAvailable: true,
  providerBrands: [],
  providersTruncated: false,
  pluginsTruncated: false,
  providers: [
    { id: "openai", name: "OpenAI", type: "openai-responses", enabled: true, isDefault: true, defaultModel: "gpt-5", defaultReasoning: "High" },
    { id: "anthropic", name: "Anthropic", type: "anthropic", enabled: true, isDefault: false, defaultModel: "claude-sonnet-4-5", defaultReasoning: null },
  ],
  plugins: [
    { id: "sample.workspace", name: "Workspace insights", version: "0.1.0", state: "Active", contributionCount: 2 },
  ],
}) });
export const modelCatalog = Object.freeze({
  providers: async () => ({ status: "unconfigured", epoch: null, providers: [], truncated: false }),
  probe: async () => ({ status: "unconfigured", epoch: null, providerId: null, availability: "Unknown" }),
  models: async () => ({ status: "unconfigured", epoch: null, providerId: null, availability: "Unknown", models: [], truncated: false }),
});
export const workspace = Object.freeze({
  snapshot: async () => snapshot,
  // The demo has no journal to list the turns of, to fork or to rewind.
  sessionTurns: async (request: WorkspaceSessionTurnsRequest) => ({ status: "unconfigured", hostEpoch: null, sessionId: request.sessionId, turns: [], total: 0, activeRunId: null, revision: null }),
  forkSession: async (request: WorkspaceForkSessionRequest) => ({ status: "unconfigured", hostEpoch: null, scope: null, projectId: null, projectPath: null, sessionId: request.sessionId,
    forkSessionId: null, title: null, turnCount: 0, prompt: null, promptTruncated: false }),
  // Nor any Claude Code session started elsewhere to list or import.
  claudeCodeSessions: async (request: WorkspaceClaudeCodeSessionsRequest) => ({ status: "no_provider", hostEpoch: null, projectId: request.projectId,
    projectPath: request.projectPath, providerId: null, offset: request.offset, sessions: [], total: 0 }),
  claudeCodeConversations: async (_request: WorkspaceClaudeCodeConversationsRequest) => ({ status: "no_provider", hostEpoch: null, providerId: null, conversations: [], total: 0 }),
  importClaudeCodeSession: async (request: WorkspaceImportClaudeCodeSessionRequest) => ({ status: "unconfigured", hostEpoch: null, projectId: request.projectId,
    projectPath: request.projectPath, cliSessionId: request.cliSessionId, sessionId: null }),
  claudeCodeSessionConversation: async (request: WorkspaceClaudeCodeSessionRequest) => ({ status: "unconfigured", hostEpoch: null, projectId: request.projectId,
    projectPath: request.projectPath, cliSessionId: request.cliSessionId, resumeCommand: null, workingDirectory: null, held: null,
    configDir: null, windows: false, home: null }),
  openClaudeCodeSessionInVsCode: async (request: WorkspaceClaudeCodeSessionRequest) => ({ status: "unconfigured", hostEpoch: null, projectId: request.projectId,
    projectPath: request.projectPath, cliSessionId: request.cliSessionId }),
  claudeCodeSessionPreview: async (request: WorkspaceClaudeCodeSessionPreviewRequest) => ({ status: "unconfigured", hostEpoch: null, projectId: request.projectId,
    projectPath: request.projectPath, cliSessionId: request.cliSessionId, sessionId: null, title: null, page: { status: "unconfigured", entries: [], next: null, tailOmitted: false } }),
  rewindSession: async (request: WorkspaceRewindSessionRequest) => ({ status: "unconfigured", hostEpoch: null, scope: null, projectId: null, projectPath: null, sessionId: request.sessionId,
    turnCount: 0, removedTurnCount: 0, prompt: null, promptTruncated: false, removedTurnsSessionId: null, removedTurnsTitle: null, rewindCount: 0, spaceId: null }),
  history: async (request: HistoryRequest): Promise<HistoryResponse> => ({
    entries: historyEntries.map(entry => ({ ...entry, sessionId: request.sessionId })),
    next: null,
    status: "ok",
    tailOmitted: false,
  }),
});

const unavailable = async () => { throw new Error("This operation requires the packaged desktop host."); };
const emptyPage = async () => ({ status: "ok", rows: [], next: null, epoch: "demo" });

export const sessionDisplay = Object.freeze({ observe: unavailable });
export const sessionRuntimeState = Object.freeze({ current: unavailable });
export const sessionRetentionProtection = Object.freeze({ publish: async () => ({ accepted: true, reason: null }) });
export const sessionUsage = Object.freeze({ read: unavailable });
export const sessionPermissions = Object.freeze({ list: emptyPage, resolve: unavailable, skillContent: unavailable });
export const conversationHeld = Object.freeze({ list: async () => ({ status: "ok", hostEpoch: "demo", sessionId: null, entries: [] }), resolve: unavailable });
// The demo has no Claude Code conversation to open in VS Code or a terminal.
const noClaudeCodeConversation = async (request: SessionClaudeCodeRequest) => ({ status: "unconfigured", epoch: null, sessionId: request.sessionId,
  cliSessionId: null, resumeCommand: null, workingDirectory: null, held: null, configDir: null, windows: false, home: null });
export const sessionOperations = Object.freeze({
  abort: unavailable, abortRun: unavailable, cancelQueue: unavailable, compact: unavailable,
  queue: unavailable, receipts: emptyPage, send: unavailable, steer: unavailable,
  // The demo has no session whose changes are reviewed before commit.
  reviewBeforeCommit: unavailable, setReviewBeforeCommit: unavailable,
  claudeCodeConversation: noClaudeCodeConversation, releaseClaudeCodeConversation: noClaudeCodeConversation,
  openInVsCode: async (request: SessionClaudeCodeRequest) => ({ status: "unconfigured", epoch: null, sessionId: request.sessionId }),
});
export const sessionAsks = Object.freeze({ answer: unavailable, cancel: unavailable, list: emptyPage, observe: unavailable });
export const sessionNotes = Object.freeze({ current: unavailable });
export const sessionPluginEvents = Object.freeze({ read: unavailable });
// The demo has no host process: nothing to repair, nothing to keep running.
export const startupConfig = Object.freeze({ read: unavailable, reload: unavailable, validate: unavailable, save: unavailable, leave: unavailable });
export const appUpdate = Object.freeze({
  check: async () => ({ status: "unavailable", packageId: "CodeAlta", currentVersion: "demo", latestVersion: null, command: null, releaseNotes: null, canInstall: false, installed: null }),
  openReleaseNotes: async () => ({ status: "unavailable" }), install: async () => ({ status: "unavailable" }), cancelInstallation: async () => ({ status: "unavailable" }),
  runtimeStatus: async () => ({ status: "current", version: "demo", minimumVersion: "demo" }), openRuntimeDownload: async () => ({ status: "unavailable" }),
});
export const desktopShell = Object.freeze({
  preferences: async () => ({ status: "unavailable", onClose: "ask", canKeepRunning: false, platform: "windows", entryAdded: false }),
  setOnClose: async () => ({ status: "unavailable", onClose: "ask", canKeepRunning: false, platform: "windows", entryAdded: false }),
  hide: async () => ({ status: "unavailable" }), exit: async () => ({ status: "unavailable" }), watch: unavailable,
  pickFolder: async () => ({ status: "unavailable", path: null }), revealEntry: async () => ({ status: "unavailable" }),
});
export const providerLogin = Object.freeze({ status: unavailable, login: unavailable, logout: unavailable });
export const providerUsage = Object.freeze({ read: unavailable });
export const projectFiles = Object.freeze({ read: unavailable, write: unavailable, list: unavailable, stat: unavailable, create: unavailable, rename: unavailable,
  delete: unavailable, image: unavailable, reveal: unavailable, openExternal: unavailable, search: unavailable, watch: unavailable });
export const projectGit = Object.freeze({ status: unavailable, changes: unavailable, commits: unavailable, file: unavailable, watch: unavailable });
export const worktrees = Object.freeze({ list: unavailable, remove: unavailable, branches: unavailable, switch: unavailable, settings: unavailable, saveSettings: unavailable,
  inventory: unavailable, removeMany: unavailable, openEditor: unavailable, openExternal: unavailable });
export const externalEditors = Object.freeze({ list: unavailable, set: unavailable, openFolder: unavailable });
export const mcpHost = Object.freeze({ status: unavailable, setEnabled: unavailable });
// A browser preview neither measures nor edits the user's real profile.
export const storage = Object.freeze({
  readSettings: async () => ({ status: "unavailable", rootPath: null, settings: null, error: null }),
  saveSettings: async () => ({ status: "unavailable", rootPath: null, settings: null, error: null }),
  scan: async () => (async function* () { yield { status: "unavailable", snapshot: null, processedFiles: "0", processedDirectories: "0" }; })(),
  watchMaintenance: unavailable,
  watchRetention: unavailable,
});
// A browser preview has no microphone permission, no model and no worker: speech to prompt does not exist there.
const noSpeech = async () => ({ status: "unavailable", supported: false, enabled: false, model: "", modelId: null, models: [], rootPath: null, send: "manual", error: null });
export const speech = Object.freeze({
  read: noSpeech, saveSettings: noSpeech, delete: noSpeech,
  install: async () => ({ status: "unavailable" }), cancelInstall: async () => ({ status: "unavailable" }),
  watch: async () => (async function* (): AsyncGenerator<{ state: string; modelId: string | null; modelName: string | null; receivedBytes: string; totalBytes: string; error: string | null }> { })(),
  start: async () => ({ status: "unavailable", id: null }), append: async () => ({ status: "unavailable" }),
  listen: async () => (async function* (): AsyncGenerator<{ state: string; finalText: string; partialText: string; error: string | null }> { })(),
  stop: async () => ({ status: "unavailable", text: "", error: null }),
});
// A browser preview downloads nothing: the Claude Code CLI is not known there.
export const claudeCli = Object.freeze({
  read: async () => ({ status: "unavailable", supported: false, version: "", size: "0", pinnedPath: null, installedPath: null, installedVersion: null, windows: false, home: null }),
  check: async () => ({ status: "unavailable", providerKey: null, claude: false, executable: null, configDir: null, supported: false, version: "", size: "0", windows: false, home: null }),
  install: async () => ({ status: "unavailable" }), cancelInstall: async () => ({ status: "unavailable" }),
  watch: async () => (async function* (): AsyncGenerator<{ state: string; receivedBytes: string; totalBytes: string; error: string | null }> { })(),
});
export const terminals = Object.freeze({ acknowledge: unavailable, attach: unavailable, close: unavailable, create: unavailable, detach: unavailable, input: unavailable,
  open: unavailable, profiles: unavailable, rename: unavailable, resize: unavailable, show: unavailable, watch: unavailable });
// The demo starts no session by itself: its automations tab says so.
const noAutomations = async () => ({ status: "unavailable", paused: false, scanned: false, items: [], faults: [], runs: [], upcoming: [] });
export const automations = Object.freeze({ list: noAutomations, refresh: noAutomations, save: unavailable, delete: unavailable, setEnabled: unavailable,
  allow: unavailable, setPaused: unavailable, run: unavailable, runs: unavailable, preview: unavailable, watch: unavailable });
// The demo has no project folder to keep tasks and plans in.
export const workItems = Object.freeze({ list: async () => ({ status: "unavailable", projects: [], settings: null }), read: unavailable, act: unavailable,
  saveSettings: unavailable, watch: unavailable });
// The demo has no checkout whose plans and changes the user reviews.
export const reviews = Object.freeze({ list: async () => ({ status: "unavailable", threads: [], rounds: [], viewed: [], request: null, readOnly: null, message: null }),
  add: unavailable, update: unavailable, delete: unavailable, resolve: unavailable, resolveAddressed: unavailable, reply: unavailable, viewed: unavailable,
  submit: unavailable, counts: async () => ({ status: "unavailable", checkouts: [] }), overview: async () => ({ status: "unavailable", projects: [] }),
  forgetCheckout: unavailable, deleteReview: unavailable, watch: unavailable });
// The demo has no host that keeps bookmarks.
export const bookmarks = Object.freeze({ list: async () => ({ status: "unavailable", bookmarks: [], truncated: false }), add: unavailable, rename: unavailable, remove: unavailable,
  resolve: unavailable, watch: unavailable });
// The demo has no index of sessions to search.
export const localSearch = Object.freeze({ query: async () => ({ status: "unavailable", hits: [], truncated: false, meaning: "off", milliseconds: 0 }),
  locate: unavailable, status: unavailable, overview: unavailable, browse: unavailable, activity: unavailable, saveSettings: unavailable, setExcluded: unavailable, reindex: unavailable,
  registerCommands: async () => ({ status: "unavailable" }), watch: unavailable, turnOnMeaning: unavailable, turnOffMeaning: unavailable, pauseMeaning: unavailable,
  cancelDownload: unavailable, deleteModel: unavailable, answerOffer: unavailable });
// The demo has no repository whose issues could be read.
export const issues = Object.freeze({ sources: async () => ({ status: "unavailable", sources: [] }), list: unavailable, read: unavailable, start: unavailable, openLink: unavailable });
export const markdownLinks = Object.freeze({ open: async () => ({ status: "unavailable", hostEpoch: "" }) });
// The demo has no host to keep the instructions for a pull request.
// The identity of a space needs a host that keeps spaces.
export const spaceIdentity = Object.freeze({ get: async () => ({ status: "unavailable", message: null, identity: null, usual: null }),
  update: async () => ({ status: "unavailable", message: null, identity: null, usual: null }), check: async () => ({ status: "unavailable", lines: [] }),
  session: async () => ({ status: "unavailable", spaceId: null, gitAuthor: null, signing: null, signOff: false, gitHubProfile: null, jiraSite: null, jiraEmail: null, cli: [] }) });
// The spaces need a host that keeps them: the demo has the default one alone.
export const spaces = Object.freeze({ list: async () => ({ status: "unavailable", spaces: [] }), activity: async () => ({ status: "unavailable", sessions: [], truncated: false }),
  create: unavailable, update: unavailable, delete: unavailable, assign: unavailable, reorder: unavailable, shown: unavailable,
  watch: unavailable, assignments: async () => ({ status: "unavailable", providers: [], mcpServers: [], plugins: [], skills: [], prompts: [] }),
  assignResource: unavailable, setAgentPrompt: unavailable, setDisabledTools: unavailable,
  sessionResources: async () => ({ status: "unavailable", spaceId: null, mcpServers: [], skills: [], prompts: [], plugins: [] }) });
// The browser demo is a single window: the page is the main window, and no other can be opened.
export const windows = Object.freeze({ current: async () => ({ status: "unavailable", window: null, pageState: null, restoreAtStartup: true, canDrag: false, windows: [] }),
  list: async () => ({ status: "unavailable", windows: [], restoreAtStartup: true }), watch: unavailable, report: async () => ({ status: "unavailable" }), moveTab: unavailable, refuseMove: unavailable,
  focusTab: unavailable, focus: unavailable, close: unavailable, open: unavailable, setIdentity: unavailable, forget: unavailable, setRestoreAtStartup: unavailable,
  displays: async () => ({ status: "unavailable", displays: [] }), dragOver: unavailable, drop: unavailable });
export const pullRequestPrompts = Object.freeze({ list: async () => ({ status: "unavailable", items: [] }), save: unavailable, delete: unavailable });
export const promptImages = Object.freeze({ read: unavailable });
export const toolCalls = Object.freeze({ read: unavailable, readJob: unavailable, observe: unavailable });
// A browser preview runs no MCP server: no tool call has a view to open.
export const mcpApps = Object.freeze({ open: unavailable, callTool: unavailable, readResource: unavailable, close: unavailable });
// The demo keeps the user's color schemes in the browser, where the desktop keeps a file for each.
const demoSchemesKey = "codealta.demo.colorSchemes.v1";
const noColors: ColorSchemeColors = { background: null, text: null, muted: null, accent: null, success: null, warning: null, danger: null };
function demoSchemes(): ColorSchemeDocument[] {
  try { const value: unknown = JSON.parse(localStorage.getItem(demoSchemesKey) ?? "[]"); return Array.isArray(value) ? value : []; } catch { return []; }
}
function keepDemoSchemes(schemes: readonly ColorSchemeDocument[]): boolean {
  try { localStorage.setItem(demoSchemesKey, JSON.stringify(schemes)); return true; } catch { return false; }
}
export const colorSchemes = Object.freeze({
  list: async () => ({ status: "ok", directory: null, schemes: demoSchemes(), problems: [] }),
  save: async (request: ColorSchemeSaveRequest) => {
    const schemes = demoSchemes(), name = (request.name ?? "").trim();
    if (name === "") return { status: "invalid", id: null, message: "A color scheme needs a name." };
    let id = request.id;
    if (!id) {
      const stem = name.toLowerCase().replace(/[^a-z0-9]+/g, "-").replace(/^-+|-+$/g, "") || "scheme";
      id = stem;
      for (let number = 2; schemes.some(scheme => scheme.id === id); number++) id = `${stem}-${number}`;
    }
    const scheme = { id, name, base: request.base ?? "blueprint", light: request.light ?? noColors, dark: request.dark ?? noColors, darker: request.darker ?? noColors };
    return keepDemoSchemes([...schemes.filter(other => other.id !== id), scheme].sort((left, right) => left.id.localeCompare(right.id)))
      ? { status: "ok", id, message: null } : { status: "write_failed", id: null, message: null };
  },
  delete: async (request: { id: string | null }) => {
    const schemes = demoSchemes();
    if (!schemes.some(scheme => scheme.id === request.id)) return { status: "not_found", message: null };
    return { status: keepDemoSchemes(schemes.filter(scheme => scheme.id !== request.id)) ? "ok" : "write_failed", message: null };
  },
  reveal: async () => ({ status: "unavailable", message: null }),
});
// The demo has no host process to log anything.
export const applicationLogs = Object.freeze({
  read: async () => ({ status: "unavailable", rows: [], captureOmitted: "0", readOmitted: 0, captureId: null, boundary: "0", grant: "" }),
  clear: async () => ({ status: "unavailable", captureId: null, clearedRows: 0, coveredOmitted: "0", boundary: "0" }),
});
export const composerStatus = Object.freeze({ read: unavailable });
export const pluginUi = Object.freeze({ contributions: unavailable, buttons: async () => ({ status: "unavailable", place: null, buttons: [] }), regions: unavailable, invokeCommand: unavailable, searchPicker: unavailable, watch: unavailable, respond: unavailable, dialogAction: unavailable });
// The canvases are tabs of plugins, which a browser without the host does not run.
export const canvases = Object.freeze({ list: async () => ({ status: "unavailable", canvases: [] }), open: unavailable, visible: unavailable, close: unavailable, closeSpace: unavailable,
  action: unavailable, describe: unavailable, watch: unavailable });
// The user guide ships beside the application: a browser without the host has none to show.
export const documentation = Object.freeze({ menu: async () => ({ status: "unavailable", home: null, items: [], pages: [], canAsk: false }), page: unavailable, image: unavailable,
  search: unavailable, ask: unavailable, watch: unavailable });
export const sessionUserInput = Object.freeze({ cancel: unavailable, list: emptyPage, resolve: unavailable });

// The demo has no host configuration file: the editor reports itself unavailable.
export const globalConfig = Object.freeze({
  read: async () => ({ status: "unavailable", content: null, revision: null }),
  reviewSettings: async () => ({ status: "unavailable", beforeCommit: false }),
  saveReviewSettings: async () => ({ status: "unavailable", beforeCommit: false }),
  commitSettings: async () => ({ status: "unavailable", coAuthor: true }),
  saveCommitSettings: async () => ({ status: "unavailable", coAuthor: true }),
  promptSuggestionSettings: async () => ({ status: "unavailable", enabled: true }),
  savePromptSuggestionSettings: async () => ({ status: "unavailable", enabled: true }),
  validate: async () => ({ valid: true, message: null, line: null, column: null }),
  save: async () => ({ status: "unavailable", revision: null, message: null, line: null, column: null, providersApplied: 0 }),
  providers: async () => ({ status: "unavailable", revision: null, defaultProvider: null, providers: [], providerTypes: [], reasoningEfforts: [], typeDefaults: [], builtIn: [] }),
  saveProvider: async () => ({ status: "unavailable", revision: null, message: null, line: null, column: null, providersApplied: 0 }),
  deleteProvider: async () => ({ status: "unavailable", revision: null, message: null, line: null, column: null, providersApplied: 0 }),
  addBuiltInProvider: async () => ({ status: "unavailable", revision: null, message: null, line: null, column: null, providersApplied: 0 }),
  sessionTitles: async () => ({ status: "unavailable", mode: null }),
  saveSessionTitles: async () => ({ status: "unavailable", mode: null }),
  disabledTools: async () => ({ status: "unavailable", tools: [], known: [] }),
  saveDisabledTools: async () => ({ status: "unavailable", tools: [], known: [] }),
});

// The demo has no host to edit: every Settings editor reports itself unavailable.
export const mcpServers = Object.freeze({
  list: async () => ({
    status: "unavailable", projectId: null, servers: [], globalConfigState: null, projectConfigState: null,
    mcpEnabled: false, policyReadError: false, omitted: 0, unsupported: [],
  }),
  save: async () => ({ status: "unavailable", message: null }),
  remove: async () => ({ status: "unavailable", message: null }),
  setEnabled: async () => ({ status: "unavailable", message: null }),
  login: unavailable,
  logout: async () => ({ status: "unavailable", removed: false }),
});
export const agentPrompts = Object.freeze({
  list: async () => ({ status: "unavailable", projectId: null, prompts: [], omitted: 0 }),
  read: async () => ({ status: "unavailable", prompt: null, message: null }),
  save: async () => ({ status: "unavailable", revision: null, message: null }),
  delete: async () => ({ status: "unavailable", revision: null, message: null }),
  compose: async () => ({ status: "unavailable", parts: [], missing: [], circular: [], systemPromptId: null, disabledTools: [], body: "", tokens: 0, truncated: false }),
});
export const skills = Object.freeze({
  list: async () => ({ status: "unavailable", projectId: null, skills: [], omitted: 0 }),
  setEnabled: async () => ({ status: "unavailable", changed: 0, message: null }),
  setAllEnabled: async () => ({ status: "unavailable", changed: 0, message: null }),
  setMatchingEnabled: async () => ({ status: "unavailable", changed: 0, message: null }),
  detail: unavailable,
  create: async () => ({ status: "unavailable", name: null, message: null }),
  delete: async () => ({ status: "unavailable", changed: 0, message: null }),
  // The demo has no MCP server: none of its skills is listed.
  listMcp: async () => ({ status: "unavailable", projectId: null, servers: [] }),
  setMcpEnabled: async () => ({ status: "unavailable", changed: 0, message: null }),
  mcpContent: async () => ({ status: "unavailable", content: null, verified: false, truncated: false, message: null }),
  forgetMcpApproval: async () => ({ status: "unavailable", changed: 0, message: null }),
});
export const plugins = Object.freeze({
  list: async () => ({ status: "unavailable", projectId: null, plugins: [], omitted: 0 }),
  setEnabled: async () => ({ status: "unavailable", message: null, applied: false }),
  reload: async () => ({ status: "unavailable", message: null, applied: false }),
  create: async () => ({ status: "unavailable", message: null, folder: null, path: null, name: null }),
  delete: async () => ({ status: "unavailable", message: null, applied: false }),
});
export const settingsFiles = Object.freeze({
  list: async () => ({ status: "unavailable", locations: [], platform: null }),
  open: async () => ({ status: "unavailable" }),
  reveal: async () => ({ status: "unavailable" }),
});
// The demo has no reminder worker.
export const reminder = Object.freeze({
  active: async () => ({ status: "unavailable", epoch: "demo", sessions: [] }),
  list: unavailable, detail: unavailable, create: unavailable, save: unavailable, delete: unavailable,
});
// The demo has no project repository: the issue picker reports that there is nothing to look up.
export const gitIssues = Object.freeze({
  search: async () => ({ status: "no_repository", epoch: "demo", provider: null, repository: null, issues: [], message: null }),
});
