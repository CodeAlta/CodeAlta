// The parts of the cloud sessions with a host of their own: the card of a session in the cloud, the start in the place
// of the welcome, the readiness of a project, and the Cloud choice of where a session works.
import { StrictMode, useState } from "react";
import { createRoot } from "react-dom/client";
import type { CloudContinueRequest as ContinueRequest, CloudReadinessResponse, CloudSessionDetailsResponse, CloudStartResponse, WorkspaceSession } from "#neoastra";
import { ShellLanguageContext } from "../shellLanguage";
import { WorkPlaceChoice, type WorkPlaceApi } from "../worktrees/WorkPlaceChoice";
import { projectFolder, type WorkPlace } from "../worktrees/worktrees";
import { CloudSessionPane } from "./CloudSessionPane";
import { CloudReadiness, CloudStartProgress } from "./CloudStart";
import type { CloudSessionsApi } from "./cloudSessions";

const id = "session_01QK5u8xRwDmhdvgs5BDTzPp";
const url = `https://claude.ai/code/${id}`;
const calls: string[] = [];
const opened: string[] = [];
let sendStatus = "ok";
let continueStatus = "failed";
const sent: { text: string; at: string }[] = [{ text: "Also add a regression test", at: "2026-10-31T21:40:00Z" }];
const readiness: CloudReadinessResponse = { status: "ok", epoch: "e", repository: true, branch: "main", unpushed: 2, changed: 3, gitHub: true, account: "subscription", windows: true };
const details = (): CloudSessionDetailsResponse => ({ status: "ok", id, url, title: "Fix the flaky login test", task: "Fix the flaky login test in src/auth/login.ts",
  branch: "main", startedAt: "2026-10-31T21:11:00Z", note: null, sent: [...sent], continuing: false, continuedAt: null, inTerminal: false });
const api: CloudSessionsApi = {
  readiness: async () => readiness,
  details: async () => details(),
  send: async request => { calls.push(`send ${request.text}`); if (sendStatus === "ok") sent.push({ text: request.text, at: "2026-10-31T22:00:00Z" });
    return { status: sendStatus, message: sendStatus === "ok" ? null : "Session is archived" }; },
  continueHere: async (request: ContinueRequest) => { calls.push(`continue worktree=${request.worktree} terminal=${request.fromTerminal}`);
    return { status: continueStatus, message: continueStatus === "failed" ? "Could not fetch the branch claude/fix-login" : null, changedFiles: 0, command: null,
      configDirectory: null, folder: null, windows: true, home: null }; },
  start: async () => { throw new Error("not used"); }, startState: async () => { throw new Error("not used"); },
  cancelStart: async () => ({ status: "ok", message: null }), bringHere: async () => { throw new Error("not used"); },
};
const session = { id: "s1", title: "Fix the flaky login test", fullTitle: "Fix the flaky login test", projectId: "p", providerKey: "claude-max", scopeKind: "project",
  cloud: { id, url, title: "Fix the flaky login test", continued: false, continuing: false } } as unknown as WorkspaceSession;
const worktreeApi: WorkPlaceApi = { branches: async () => ({ status: "ok", branches: [], busy: false }) as never, list: async () => ({ status: "unavailable" }) as never };

const fixture: Record<string, unknown> = { calls, opened,
  setSend: (status: string) => { sendStatus = status; }, setContinue: (status: string) => { continueStatus = status; },
  setTheme: (theme: string) => { document.documentElement.dataset.theme = theme; document.documentElement.classList.toggle("bp6-dark", theme === "dark"); } };
Object.assign(window, { cloud: fixture });
document.documentElement.dataset.theme = "dark";
document.documentElement.classList.add("bp6-dark");

function Start() {
  const [outcome, setOutcome] = useState<CloudStartResponse | null>(null);
  const [step, setStep] = useState<string | null>("starting");
  fixture.startStep = setStep;
  fixture.startOutcome = (status: string) => setOutcome({ status, epoch: "e", requestId: "r", sessionId: null, cloudSessionId: id, url: status === "unrecorded" ? url : null,
    message: status === "failed" ? "Error: You must be logged in to claude.ai" : null, terminalId: status === "failed" ? "t1" : null, step: null });
  return <div data-start><CloudStartProgress step={step} outcome={outcome} onStop={() => calls.push("stop")} onShowTerminal={terminal => calls.push(`terminal ${terminal}`)}
    onOpenLink={link => opened.push(link)} onBringHere={link => calls.push(`bring ${link}`)} onClose={() => calls.push("close")} /></div>;
}

function Place() {
  const [place, setPlace] = useState<WorkPlace>(projectFolder);
  fixture.place = () => place;
  return <div data-place><WorkPlaceChoice epoch="e" projectId="p" branch="main" place={place} onPlace={setPlace} api={worktreeApi} cloud /></div>;
}

createRoot(document.getElementById("root")!).render(<StrictMode>
  <ShellLanguageContext.Provider value={{ locale: "en", choice: "en", setLanguage: () => { } }}>
    <div className="ide-shell" style={{ display: "grid", gridTemplateColumns: "minmax(0, 1fr) 360px", gap: 12, height: "100vh" }}>
      <div className="session-workspace" data-pane style={{ display: "flex", flexDirection: "column", minHeight: 0, minWidth: 0 }}>
        <CloudSessionPane epoch="e" session={session} provider="Claude Code (Max)" projectPath="C:\\code\\app" canMutate api={api}
          onOpenLink={link => opened.push(link)} onChanged={() => calls.push("changed")} onTerminal={async () => null} continueRequest={null} />
      </div>
      <div style={{ overflow: "auto" }}>
        <Start />
        <div data-readiness><CloudReadiness epoch="e" projectId="p" providerKey="claude-max" provider="Claude Code (Max)" api={api} onLearnMore={() => calls.push("learn")} /></div>
        <Place />
      </div>
    </div>
  </ShellLanguageContext.Provider>
</StrictMode>);
