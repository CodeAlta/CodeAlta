/* Local Vite-only demo backend. The packaged desktop always resolves #neoastra to the generated bridge. */
import type {
  BootStatus,
  ConfigurationSnapshot,
  HistoryRequest,
  HistoryResponse,
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
    { id: "demo-active", title: "Build the desktop workspace", fullTitle: "Build the desktop workspace", fullTitleTruncated: false, parentSessionId: null, scopeKind: "project", projectId: "codealta", lineageIssue: null, workspacePath: "C:\\code\\CodeAlta", providerKey: "openai", updatedAt: "A few seconds ago" },
    { id: "demo-review", title: "Review runtime ownership", fullTitle: "Review runtime ownership", fullTitleTruncated: false, parentSessionId: null, scopeKind: "project", projectId: "codealta", lineageIssue: null, workspacePath: "C:\\code\\CodeAlta", providerKey: "openai", updatedAt: "18 minutes ago" },
    { id: "demo-docs", title: "Improve onboarding", fullTitle: "Improve onboarding", fullTitleTruncated: false, parentSessionId: null, scopeKind: "project", projectId: "docs", lineageIssue: null, workspacePath: "C:\\work\\docs", providerKey: "anthropic", updatedAt: "Yesterday" },
    { id: "demo-global", title: "Explore CodeAlta", fullTitle: "Explore CodeAlta", fullTitleTruncated: false, parentSessionId: null, scopeKind: "global", projectId: null, lineageIssue: null, workspacePath: null, providerKey: null, updatedAt: "Last week" },
  ],
};

const historyEntries: HistoryResponse["entries"] = [
  {
    activityId: null, bodyOmitted: false, contentId: "user-1", eventType: "contentCompleted", kind: "User",
    details: null, detailsTruncated: false, interactionId: null, name: null, offset: "0", parentActivityId: null, phase: null, providerId: "demo", runId: "demo-run",
    sessionId: "demo-active", text: "Create a usable desktop workspace I can run locally.", textTruncated: false,
    timestamp: "2026-09-22T10:12:00Z",
  },
  {
    activityId: null, bodyOmitted: false, contentId: "assistant-1", eventType: "contentCompleted", kind: "Assistant",
    details: null, detailsTruncated: false, interactionId: null, name: null, offset: "1", parentActivityId: null, phase: null, providerId: "demo", runId: "demo-run",
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
  productName: "CodeAlta Desktop Demo",
  state: "demo",
  version: "local preview",
};

export const boot = Object.freeze({ status: async () => bootStatus });
export const configuration = Object.freeze({ snapshot: async (): Promise<ConfigurationSnapshot> => ({
  providerRuntimeAvailable: true,
  pluginRuntimeAvailable: true,
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
export const workspace = Object.freeze({
  snapshot: async () => snapshot,
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
export const sessionPermissions = Object.freeze({ list: emptyPage, resolve: unavailable });
export const sessionOperations = Object.freeze({
  abort: unavailable, abortRun: unavailable, cancelQueue: unavailable, compact: unavailable,
  queue: unavailable, receipts: emptyPage, send: unavailable, steer: unavailable,
});
export const sessionAsks = Object.freeze({ answer: unavailable, cancel: unavailable, list: emptyPage, observe: unavailable });
export const sessionNotes = Object.freeze({ current: unavailable });
export const sessionUserInput = Object.freeze({ cancel: unavailable, list: emptyPage, resolve: unavailable });
