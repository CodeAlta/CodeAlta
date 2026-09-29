import { createElement } from "react";
import { createRoot } from "react-dom/client";
import { AskPanel } from "./AskPanel";
import { archivedProjectScope, SessionComposerGate } from "./ArchivedScopeGates";
import { createAskActions } from "./sessionAsks";
import { createMutationCapability } from "./sessionOperations";
import type { WorkspaceSession, WorkspaceSnapshot } from "#neoastra";
import { ShellLanguageContext } from "./shellLanguage";
import type { Locale } from "./localization";

const epoch = "aaaaaaaa-aaaa-4aaa-8aaa-aaaaaaaaaaaa";
let currentEpoch = epoch;
let currentSession = "session-one";
let archived = false;
let locale: Locale = "en";
const requests: unknown[] = [];
const settlers: { resolve: (value: unknown) => void; reject: (error: Error) => void }[] = [];
(window as Window & { askFixtureList?: (request: unknown) => Promise<unknown> }).askFixtureList = request => {
  requests.push(request);
  return new Promise((resolve, reject) => settlers.push({ resolve, reject }));
};
const answers: unknown[] = [];
const cancellations: unknown[] = [];
const observations: unknown[] = [];
Object.assign(window, { askFixtureObserve: (request: unknown) => { observations.push(request); return Promise.reject(new Error("Fixture observation unavailable")); } });
const answerFailures: ((error: Error) => void)[] = [];
const cancelFailures: ((error: Error) => void)[] = [];
const actions = createAskActions(async request => { answers.push(request); return new Promise((_, reject) => answerFailures.push(reject)); },
  async request => { cancellations.push(request); return new Promise((_, reject) => cancelFailures.push(reject)); });
const capability = createMutationCapability(epoch);
const root = createRoot(document.getElementById("app")!);
function render() {
  const session: WorkspaceSession = { id: currentSession, title: currentSession, fullTitle: currentSession,
    messageCount: null, createdAt: null, fullTitleTruncated: false, parentSessionId: null, scopeKind: "project", projectId: "project", lineageIssue: null,
    workspacePath: "/fixture/project", providerKey: "fixture", updatedAt: "2026-09-25T00:00:00Z" };
  const snapshot: WorkspaceSnapshot = { configured: true, projects: [{ id: "project", name: "Project", path: "/fixture/project", archived }],
    sessions: [session], projectsTruncated: false, sessionsTruncated: false, displayTextTruncated: false };
  root.render(createElement(ShellLanguageContext.Provider, { value: { locale, choice: locale, setLanguage: value => { locale = value as Locale; render(); } } }, createElement("div", null,
    createElement(SessionComposerGate, { snapshot, projectId: "project", session, epoch: currentEpoch,
      owned: createElement("p", { id: "owned-ask-gate" }, "Owned workspace"),
      readOnly: createElement("p", { id: "archived-ask" }, "Read-only archive; ask editor unavailable"), recovery: null }),
    !archivedProjectScope(snapshot, "project") && createElement(AskPanel, { epoch: currentEpoch, sessionId: currentSession, actions, capability }))));
}
Object.assign(window, { askFixture: {
  epoch, requests, answers, cancellations, observations,
  language(value: Locale) { locale = value; render(); },
  retained() { return actions.forSession(currentSession); },
  invalidate() { capability.observe({ status: "stale_epoch", epoch }); },
  page(head: unknown, options: { status?: string; sessionId?: string; epoch?: string } = {}) {
    settlers.shift()?.resolve({ status: options.status ?? "ok", hostEpoch: options.epoch ?? currentEpoch,
      sessionId: options.sessionId ?? currentSession, head, latest: null, hasMore: false });
  },
  fail() { settlers.shift()?.reject(new Error("Fixture read failed")); },
  failAnswer() { answerFailures.shift()?.(new Error("Original answer waiter lost")); },
  failCancel() { cancelFailures.shift()?.(new Error("Original cancel waiter lost")); },
  scope(value: string, host = currentEpoch) { currentSession = value; currentEpoch = host; render(); },
  archive(value: boolean) { archived = value; render(); },
} });
render();
