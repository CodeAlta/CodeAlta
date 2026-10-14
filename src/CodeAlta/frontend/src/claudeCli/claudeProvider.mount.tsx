import { StrictMode } from "react";
import { createRoot } from "react-dom/client";
import type { ClaudeCliInstallState, ClaudeCliStatusResponse, GlobalConfigProvider, GlobalConfigSaveProviderRequest } from "#neoastra";
import { ProviderSettings } from "../ProviderSettings";

// A provider of Claude Code with sessions, a CLI that is not downloaded yet, and the Claude Code of the user.
const provider: GlobalConfigProvider = { key: "claude-code", type: "claude-code", enabled: true, displayName: null, effectiveName: "Claude Code", model: null,
  reasoningEffort: null, apiUrl: null, effectiveApiUrl: null, apiKeyEnv: null, hasApiKey: false,
  defaults: { displayName: "Claude Code", model: null, reasoningEffort: null, apiUrl: null, apiKeyEnv: null }, icon: null, color: null, anthropicApiKey: null,
  permissionMode: null, promptSuggestions: false, configDir: null, command: null };
const status: ClaudeCliStatusResponse = { status: "ok", supported: true, version: "2.1.292", size: "113796621", pinnedPath: null,
  installedPath: "C:\\Users\\me\\.local\\bin\\claude.exe", installedVersion: "2.1.300", windows: true, home: "C:\\Users\\me" };
const state = { saved: [] as GlobalConfigSaveProviderRequest[], duplicated: [] as string[], installs: 0, reads: 0,
  tell: (_state: ClaudeCliInstallState) => { } };
const result = { status: "ok", message: null, revision: "revision", line: null, column: null, providersApplied: 1, key: null as string | null };
const config = {
  providers: async () => ({ status: "ok", revision: "revision", defaultProvider: "claude-code", startingProvider: "claude-code", providers: [provider],
    providerTypes: ["claude-code"], reasoningEfforts: [], permissionModes: [], typeDefaults: [], builtIn: [], unsupported: [] }),
  saveProvider: async (request: GlobalConfigSaveProviderRequest) => { state.saved.push(request); return result; },
  deleteProvider: async () => result, addBuiltInProvider: async () => result,
  duplicateProvider: async (request: { key: string | null }) => { state.duplicated.push(request.key ?? ""); return { ...result, key: "claude-code-2" }; },
};
const cli = {
  read: async () => { state.reads++; return status; },
  install: async () => { state.installs++; return { status: "ok" }; },
  cancelInstall: async () => ({ status: "ok" }),
  // The states the test tells, as the host streams them.
  watch: async (_request: object, options?: { signal?: AbortSignal }) => {
    const waiting: ClaudeCliInstallState[] = [{ state: "idle", receivedBytes: "0", totalBytes: "0", error: null }];
    let wake = () => { };
    state.tell = value => { waiting.push(value); wake(); };
    options?.signal?.addEventListener("abort", () => wake(), { once: true });
    return (async function* () {
      while (!options?.signal?.aborted) {
        if (waiting.length === 0) await new Promise<void>(resolve => { wake = resolve; });
        while (waiting.length > 0) yield waiting.shift()!;
      }
    })();
  },
};
Object.assign(window, { claudeFixture: { state, status } });
createRoot(document.getElementById("root")!).render(<StrictMode><ProviderSettings epoch="epoch" config={config} cli={cli}
  readRuntime={async () => ({ status: "ok", epoch: "epoch", providers: [], truncated: false })}
  probe={async request => ({ status: "ok", epoch: "epoch", providerId: request.providerId, availability: "Ready", reason: null })}
  filesApi={{ list: async () => ({ status: "ok", platform: "windows", locations: [] }), open: async () => ({ status: "ok" }), reveal: async () => ({ status: "ok" }) }}
  providerSessions={key => key === "claude-code" ? 2 : 0} onOpenModels={() => {}} onOpenConfiguration={() => {}} /></StrictMode>);
