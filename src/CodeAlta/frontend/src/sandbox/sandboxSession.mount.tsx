// Disposable page: what a sandboxed session shows above and in its composer: the request to run a command outside its
// sandbox, and the Sandbox row of the Agent chip with its mark. Driven by sandboxSession.browser.test.ts through
// `sandboxSessionFixture`.
import { StrictMode, useState } from "react";
import { createRoot } from "react-dom/client";
import type { SandboxProfileItem, SandboxSetScopeRequest, SessionOptionsChoice } from "#neoastra";
import { CommandPermissionPanel } from "../CommandPermissionPanel";
import { ComposerSelectionFields } from "../ComposerSurface";
import { createPermissionReviewer } from "../sessionPermissions";
import { SessionOptionsFields } from "../sessionSettings/SessionOptionsFields";
import { changedSessionOptions, sessionOptionsOf } from "../sessionSettings/sessionOptions";
import { ShellLanguageContext } from "../shellLanguage";
import { ProjectSandboxPopover } from "./ProjectSandboxPopover";
import type { SessionSandboxSummary } from "./sandboxChoices";
import type { SandboxScopeApi } from "./SandboxScopeControls";
import { SessionSandboxSelect } from "./SessionSandboxSelect";

document.documentElement.classList.add("bp6-dark");

type List = Parameters<typeof createPermissionReviewer>[0];
type Resolve = Parameters<typeof createPermissionReviewer>[1];
const epoch = "11111111-1111-4111-8111-111111111111";
const decisions: Parameters<Resolve>[0][] = [];
const chosen: (string | null)[] = [];
const optionsChosen: (SessionOptionsChoice | null)[] = [];
let answered = false;
const list: List = async request => ({ status: "ok", hostEpoch: epoch, sessionId: request.sessionId, hasMore: false, entries: answered ? [] : [{
  handle: { operationId: epoch, runtimeInstanceId: epoch, attachmentGeneration: "1", sessionId: request.sessionId, runId: null, interactionId: "request",
    attemptId: "22222222-2222-4222-8222-222222222222" },
  providerId: "codex", kind: "commandExecution", command: "dotnet nuget push bin/Lib.1.2.0.nupkg --source nuget.org", workingDirectory: "C:\\code\\Lib", grantRoot: null,
  reason: "Publishing needs the API key in your NuGet.Config, which the sandbox cannot read.", shortened: false, outsideSandbox: true, skill: null }] });
const resolve: Resolve = async request => { decisions.push(request); answered = true; return { status: "resolved", hostEpoch: epoch, handle: request.handle }; };
const reviewer = createPermissionReviewer(list, resolve);
// The sandbox of a project, chosen beside its row: the host has `strict` for it until the popover changes it.
const scopes: SandboxSetScopeRequest[] = [];
let projectSandbox: string | null = "strict";
const scopeApi: SandboxScopeApi = {
  read: async () => ({ status: "ok", allSessions: null, profiles: [{ name: "workspace" }, { name: "strict" }] as SandboxProfileItem[],
    scopes: projectSandbox ? [{ kind: "project", id: "lib", name: "Lib", modelId: null, profile: projectSandbox }] : [],
    availability: { available: true, backend: "Windows process container", reason: null, setup: null, networks: [] } }),
  setScope: async request => { scopes.push(request); projectSandbox = request.sandbox; return { status: "ok", message: null }; },
};
Object.assign(window, { sandboxSessionFixture: { decisions, chosen, scopes, optionsChosen } });

function ProjectRow() {
  const [open, setOpen] = useState(true);
  Object.assign(window, { projectSandboxOpen: open });
  return <ul><li className="project-action-row" style={{ position: "relative", width: 300 }}><button type="button">Lib</button>
    {open && <ProjectSandboxPopover epoch={epoch} projectId="lib" projectName="Lib" api={scopeApi} onClose={() => setOpen(false)} />}</li></ul>;
}

// The session follows the settings, which give `workspace` through its provider, until the row gives it its own. Its
// options (sub-sessions) are under More options.
function Composer() {
  const [own, setOwn] = useState<string | null>(null);
  const [problem, setProblem] = useState<string | null>(null);
  const [options, setOptions] = useState<SessionOptionsChoice | null>(null);
  Object.assign(window, { breakProfile: () => setProblem("There is no sandbox profile 'gone'.") });
  const profile = own === null ? "workspace" : own === "off" ? null : own;
  const sandbox: SessionSandboxSummary = { profile, source: own === null ? "provider" : "session", problem };
  return <ComposerSelectionFields sessionId="s" agent={<span />} provider={<span />} model={<span />} reasoning={<span />}
    summary={{ agent: "Default", provider: "Codex", model: "GPT-6.1", reasoning: "Low", providerKey: "codex", modelId: "gpt-6.1", sandbox,
      subSessions: sessionOptionsOf(options).subSessions }}
    sandbox={<SessionSandboxSelect id="composer-sandbox-s" own={own} inherited="workspace" choices={["off", "workspace", "strict"]}
      onChange={value => { chosen.push(value); setOwn(value); }} />}
    options={{ changed: changedSessionOptions(options),
      content: <SessionOptionsFields id="s" value={options} onChange={value => { optionsChosen.push(value); setOptions(value); }} /> }} />;
}

createRoot(document.getElementById("root")!).render(<StrictMode>
  <ShellLanguageContext.Provider value={{ locale: "en", choice: "en", setLanguage: () => {} }}>
    <div className="ide-shell" style={{ padding: 12, height: "100%", boxSizing: "border-box", display: "flex", flexDirection: "column", justifyContent: "space-between" }}>
      <div className="composer-region">
        <CommandPermissionPanel reviewer={reviewer} epoch={epoch} sessionId="session" canReview={() => true} running />
      </div>
      <ProjectRow />
      <div className="prompt-surface" style={{ marginTop: 400 }}><Composer /></div>
    </div>
  </ShellLanguageContext.Provider>
</StrictMode>);
