// Disposable page: the Automations tab with an automation that decides what its runs do without a person, a run of this
// computer and one of another machine that wait for the person, and a try that recorded its calls. Driven by
// automationsPanel.browser.test.ts through `automationsFixture`: what the page asks is recorded.
import { StrictMode, useState } from "react";
import { createRoot } from "react-dom/client";
import type { AutomationItem, AutomationRunItem } from "#neoastra";
import { ShellLanguageContext } from "../shellLanguage";
import { AutomationsPanel } from "./AutomationsPanel";
import type { AutomationsHub, AutomationsState } from "./automationsHub";

document.documentElement.classList.add("bp6-dark");
document.documentElement.dataset.theme = "dark";

const calls: string[] = [];
const item: AutomationItem = {
  id: "0199f4c2-6d1e-7c3a-b5f0-2f9c8e4a1d77", name: "Incident diagnosis", enabled: true, prompt: "Diagnose the incident of the shop.", projectId: null, projectName: null,
  projectFolder: null, storeProjectId: null, file: "C:/Users/me/.alta/config.toml", provider: null, model: null, effort: null, agent: null, permissionMode: null,
  reviewBeforeCommit: null, sandbox: null, catchUp: false, triggers: [], problem: null, nextRunAt: null, running: true, lastRun: null, repository: null, watchProblem: null,
  allowed: true, script: null, scriptArguments: null, options: null, runsOn: null, placement: null,
  autonomy: { allowedTools: ["k8s_get_deployment", "k8s_restart_pod"], maxCallsPerHour: 3, approvalTimeout: 30, onTimeout: "stop", approvers: "oncall",
    tools: [{ name: "k8s_restart_pod", version: "1.4.2", hash: "sha256:3f", bounds: [{ argument: "namespace", values: ["shop"], min: null, max: null },
      { argument: "replicas", values: null, min: 1, max: 5 }], preconditions: [{ tool: "k8s_get_deployment", arguments: ["namespace"], expect: "available" }] }] },
};
const run = (id: string, change: Partial<AutomationRunItem>): AutomationRunItem => ({ id, automationId: item.id, name: item.name, sessionId: null, projectId: null,
  startedAt: new Date(Date.now() - 20 * 60_000).toISOString(), endedAt: null, trigger: "manual", detail: null, status: "running", message: null, machineId: null, reply: null,
  output: null, calls: null, ...change });
let runs: AutomationRunItem[] = [
  run("r-local", { status: "waiting", sessionId: "s1", trigger: "incident_io", detail: "INC-42 Checkout is down" }),
  run("r-remote", { status: "waiting", sessionId: "remote-s", machineId: "mac", trigger: "incident_io", detail: "INC-43 Search is slow" }),
  run("r-try", { status: "completed", trigger: "try", endedAt: new Date().toISOString(),
    calls: ['k8s_get_deployment {"namespace":"shop"}', 'k8s_restart_pod {"namespace":"shop","pod":"api-7-6d9f8c7b5-xk2lp","replicas":3,"reason":"CrashLoopBackOff for 14 minutes"}'] }),
];
const listeners = new Set<() => void>();
let state: AutomationsState = { loaded: true, available: true, paused: false, scanned: true, items: [item], faults: [], runs, upcoming: [] };
const changed = () => { state = { ...state, runs: [...runs] }; for (const listener of listeners) listener(); };
const hub = {
  subscribe: (listener: () => void) => { listeners.add(listener); return () => listeners.delete(listener); },
  getSnapshot: () => state, refresh: async () => { }, connect: () => { },
  runs: async () => runs,
  run: async () => ({ ok: true, run: null }),
  tryRun: async (id: string, index: number) => { calls.push(JSON.stringify({ tryRun: id, index })); return { ok: true, run: null }; },
  takeOver: async (id: string, runId: string) => {
    calls.push(JSON.stringify({ takeOver: runId }));
    runs = runs.map(other => other.id === runId ? { ...other, status: "running", message: "Taken over by a person." } : other);
    changed();
    return { ok: true, message: null, sessionId: runId === "r-local" ? "s1" : null };
  },
} as unknown as AutomationsHub;

function Fixture() {
  const [width, setWidth] = useState(1600);
  Object.assign(window, { automationsFixture: { calls, setWidth } });
  return <div className="ide-shell" style={{ width, height: "100%" }}>
    <AutomationsPanel hub={hub} projects={[]} sessions={[]} projectId={null} providers={[]} epoch={null} visible
      onActivate={() => { }} onOpenSession={(sessionId) => calls.push(JSON.stringify({ open: sessionId }))} />
  </div>;
}

createRoot(document.getElementById("root")!).render(<StrictMode>
  <ShellLanguageContext.Provider value={{ locale: "en", choice: "en", setLanguage: () => { } }}>
    <Fixture />
  </ShellLanguageContext.Provider>
</StrictMode>);
