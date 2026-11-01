// The sessions under a session that wait for a permission, above its prompt, as the pane of that session shows them.
import { StrictMode, useRef } from "react";
import { createRoot } from "react-dom/client";
import { ownPermissionChoice } from "./CommandPermissionPanel";
import { createPermissionReviewer } from "./sessionPermissions";
import { SessionLinksContext, type SessionLinks } from "./SessionReference";
import { ShellLanguageContext } from "./shellLanguage";
import { WaitingSubSessionsPanel } from "./WaitingSubSessionsPanel";

type List = Parameters<typeof createPermissionReviewer>[0];
type Resolve = Parameters<typeof createPermissionReviewer>[1];
const epoch = "11111111-1111-4111-8111-111111111111";
const attempts: Record<string, string> = { child: "22222222-2222-4222-8222-222222222222", grandchild: "33333333-3333-4333-8333-333333333333" };
const sessions = [{ sessionId: "child", title: "Fix the parser" }, { sessionId: "grandchild", title: "Run the tests" }];
const reads: string[] = [];
const decisions: Parameters<Resolve>[0][] = [];
const answered = new Set<string>();
// Each session has one request, until it is answered.
const list: List = async request => {
  reads.push(request.sessionId);
  return { status: "ok", hostEpoch: epoch, sessionId: request.sessionId, hasMore: false, entries: answered.has(request.sessionId) ? [] : [{
    handle: { operationId: epoch, runtimeInstanceId: epoch, attachmentGeneration: "1", sessionId: request.sessionId, runId: null, interactionId: "request",
      attemptId: attempts[request.sessionId] },
    providerId: "provider", kind: "commandExecution", command: `echo ${request.sessionId}`, workingDirectory: "Q:\\work", grantRoot: null, reason: null, shortened: false, outsideSandbox: false, skill: null, browser: null }] };
};
const resolve: Resolve = async request => {
  decisions.push(request);
  answered.add(request.handle.sessionId);
  return { status: "resolved", hostEpoch: epoch, handle: request.handle };
};
const links: SessionLinks = { title: id => sessions.find(session => session.sessionId === id)?.title ?? null, open: () => { } };
// What takes the keyboard when the pane is shown: the choice of its own request, as the window asks for it.
Object.assign(window, { waitingSubSessions: { reads, decisions, ownChoice: () => ownPermissionChoice(document.querySelector(".composer-region")) } });

function Fixture() {
  const prompt = useRef<HTMLTextAreaElement>(null);
  return <div className="composer-region">
    <WaitingSubSessionsPanel sessions={sessions} epoch={epoch} canReview={() => true} createReviewer={() => createPermissionReviewer(list, resolve)}
      onAnswered={() => prompt.current?.focus()} />
    <textarea id="session-prompt" ref={prompt} />
  </div>;
}

createRoot(document.getElementById("root")!).render(<StrictMode>
  <ShellLanguageContext.Provider value={{ locale: "en", choice: "en", setLanguage: () => { } }}>
    <SessionLinksContext.Provider value={links}><Fixture /></SessionLinksContext.Provider>
  </ShellLanguageContext.Provider>
</StrictMode>);
