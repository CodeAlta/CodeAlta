// Disposable page: Settings > Sandbox over a host that answers at once, with a check whose steps come as the test lets
// them. Driven by sandboxSettings.browser.test.ts through `sandboxFixture`.
import { StrictMode } from "react";
import { createRoot } from "react-dom/client";
import type { SandboxCheckEvent, SandboxProfileItem, SandboxSaveProfileRequest, SandboxScopeItem, SandboxSettingsResponse } from "#neoastra";
import { ShellLanguageContext } from "../shellLanguage";
import { SandboxSettings } from "./SandboxSettings";
import type { SandboxApi } from "./sandboxChoices";

document.documentElement.classList.add("bp6-dark");

const profile = (change: Partial<SandboxProfileItem> & { name: string }): SandboxProfileItem => ({ builtIn: false, extends: null, network: null, outside: null,
  hosts: [], write: [], read: [], deny: [], localPorts: [], env: [], mcpServers: [], problem: null, ...change });
// What the host holds.
const held = {
  allSessions: null as string | null,
  profiles: [
    profile({ name: "workspace", builtIn: true, network: "hosts", outside: "ask", hosts: ["nuget.org", "*.nuget.org", "github.com"], read: ["~/.dotnet"] }),
    profile({ name: "strict", builtIn: true, network: "off", outside: "never" }),
    profile({ name: "broken", extends: "gone", problem: "The sandbox profile 'broken' extends 'gone', which does not exist." }),
  ],
  scopes: [{ kind: "provider", id: "ollama", name: "Ollama", modelId: null, profile: "strict" }, { kind: "space", id: "work", name: "Work", modelId: null, profile: "off" },
    { kind: "model", id: "codex", name: "Codex", modelId: "gpt-6.1", profile: "workspace" }] as SandboxScopeItem[],
};
const state = {
  reads: 0, savedAll: [] as (string | null)[], savedProfiles: [] as SandboxSaveProfileRequest[], deleted: [] as string[], checks: [] as string[], opened: [] as SandboxScopeItem[],
  // The check stops after its first step until the test lets it go on.
  release: () => {},
};
const listing = (): SandboxSettingsResponse => ({ status: "ok", allSessions: held.allSessions, profiles: held.profiles, scopes: held.scopes,
  availability: { available: true, backend: "Windows process container", reason: null, setup: null, networks: ["hosts", "off", "on"] } });
const ok = { status: "ok", message: null };
const api: SandboxApi = {
  read: async () => { state.reads++; return listing(); },
  saveAllSessions: async request => { state.savedAll.push(request.profile); held.allSessions = request.profile; return ok; },
  saveProfile: async request => {
    state.savedProfiles.push(request);
    held.profiles = [...held.profiles.filter(item => item.name !== (request.originalName ?? request.profile.name)), { ...request.profile, problem: null }];
    return ok;
  },
  deleteProfile: async request => { state.deleted.push(request.name!); held.profiles = held.profiles.filter(item => item.name !== request.name); return ok; },
  setScope: async () => ok,
  check: async request => {
    state.checks.push(request.profile!);
    const gate = new Promise<void>(resolve => { state.release = resolve; });
    return (async function* (): AsyncGenerator<SandboxCheckEvent> {
      yield { status: "step", id: "available", passed: true, detail: "Windows process container" };
      await gate;
      yield { status: "step", id: "start", passed: true, detail: "310 ms" };
      yield { status: "step", id: "dotnet", passed: true, detail: "10.0.100" };
      yield { status: "step", id: "secrets", passed: false, detail: "The file was read." };
      yield { status: "done", id: null, passed: false, detail: null };
    })();
  },
};
Object.assign(window, { sandboxFixture: state });

createRoot(document.getElementById("root")!).render(<StrictMode>
  <ShellLanguageContext.Provider value={{ locale: "en", choice: "en", setLanguage: () => {} }}>
    <div className="ide-shell" style={{ padding: 12, overflow: "auto", height: "100%", boxSizing: "border-box" }}>
      <SandboxSettings epoch="epoch" api={api} onOpenScope={scope => state.opened.push(scope)} />
    </div>
  </ShellLanguageContext.Provider>
</StrictMode>);
