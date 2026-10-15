import { StrictMode, useState } from "react";
import { createRoot } from "react-dom/client";
import type { ClaudeCliInstallState, ClaudeCliStatusResponse, GlobalConfigProvider, GlobalConfigSaveProviderRequest } from "#neoastra";
import { ProviderSettings } from "../ProviderSettings";
import { readAssignments, readSpaces, type SpaceResourceKind } from "../spaces/spaces";

// A provider of Claude Code with sessions, a CLI that is not downloaded yet, and the Claude Code of the user.
const provider: GlobalConfigProvider = { key: "claude-code", type: "claude-code", enabled: true, displayName: null, effectiveName: "Claude Code", model: null,
  reasoningEffort: null, apiUrl: null, effectiveApiUrl: null, apiKeyEnv: null, hasApiKey: false,
  defaults: { displayName: "Claude Code", model: null, reasoningEffort: null, apiUrl: null, apiKeyEnv: null }, icon: null, color: null, anthropicApiKey: null,
  permissionMode: null, promptSuggestions: false, configDir: null, command: null };
const missing: GlobalConfigProvider = { ...provider, key: "claude-tools", effectiveName: "Claude Tools", command: "D:\\nowhere\\claude.exe" };
const status: ClaudeCliStatusResponse = { status: "ok", supported: true, version: "2.1.292", size: "113796621", pinnedPath: null,
  installedPath: "C:\\Users\\me\\.local\\bin\\claude.exe", installedVersion: "2.1.300", windows: true, home: "C:\\Users\\me" };
const state = { saved: [] as GlobalConfigSaveProviderRequest[], duplicated: [] as string[], installs: 0, reads: 0,
  tell: (_state: ClaudeCliInstallState) => { } };
const result = { status: "ok", message: null, revision: "revision", line: null, column: null, providersApplied: 1, key: null as string | null };
const config = {
  providers: async () => ({ status: "ok", revision: "revision", defaultProvider: "claude-code", startingProvider: "claude-code", providers: [provider, missing],
    providerTypes: ["claude-code"], reasoningEfforts: [], permissionModes: [], typeDefaults: [], builtIn: [], unsupported: [] }),
  saveProvider: async (request: GlobalConfigSaveProviderRequest) => { state.saved.push(request); return result; },
  deleteProvider: async () => result, addBuiltInProvider: async () => result,
  duplicateProvider: async (request: { key: string | null }) => { state.duplicated.push(request.key ?? ""); return { ...result, key: "claude-code-2" }; },
};
const cli = {
  read: async () => { state.reads++; return status; },
  check: async ({ providerKey }: { providerKey: string | null }) => ({ status: "ok", providerKey, claude: true,
    executable: providerKey === "claude-tools" ? null : status.pinnedPath ?? status.installedPath, configDir: null, supported: true,
    version: status.version, size: status.size, windows: true, home: status.home }),
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
// Two spaces: the second account works in Work alone.
const spaces = readSpaces([{ id: "default", name: "Default", description: null, icon: null, color: null, isDefault: true, projectIds: [], file: null, agentPrompt: null, disabledTools: [], ownTiers: false, identityEmail: null, identityParts: [] },
  { id: "work", name: "Work", description: null, icon: "briefcase", color: null, isDefault: false, projectIds: [], file: null, agentPrompt: null, disabledTools: [], ownTiers: false, identityEmail: null, identityParts: [] }]);
const assigned: { kind: string; key: string; spaces: readonly string[] }[] = [];
Object.assign(window, { claudeFixture: { state, status, assigned } });

function Page() {
  const [providers, setProviders] = useState<{ key: string; spaces: string[] }[]>([{ key: "claude-tools", spaces: ["work"] }]);
  const assignments = readAssignments({ status: "ok", providers, mcpServers: [], plugins: [], skills: [], prompts: [] });
  const spaceScope = { spaces, assignments, assign: async (kind: SpaceResourceKind, key: string, next: readonly string[]) => {
    assigned.push({ kind, key, spaces: next });
    setProviders(value => [...value.filter(item => item.key !== key), { key, spaces: [...next] }]);
    return { ok: true, status: "ok", message: null, space: null };
  } };
  return <ProviderSettings epoch="epoch" config={config} cli={cli} spaceScope={spaceScope}
    readRuntime={async () => ({ status: "ok", epoch: "epoch", providers: [], truncated: false })}
    probe={async request => ({ status: "ok", epoch: "epoch", providerId: request.providerId, availability: "Ready", reason: null })}
    filesApi={{ list: async () => ({ status: "ok", platform: "windows", locations: [] }), open: async () => ({ status: "ok" }), reveal: async () => ({ status: "ok" }) }}
    providerSessions={key => key === "claude-code" ? 2 : 0} onOpenModels={() => {}} onOpenConfiguration={() => {}} />;
}
createRoot(document.getElementById("root")!).render(<StrictMode><Page /></StrictMode>);
