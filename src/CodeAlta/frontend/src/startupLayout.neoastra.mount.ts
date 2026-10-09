// Empty, owned first-start fixture. All services are in-browser and inert: no native bridge,
// catalog, provider initialization or user profile is used.
import type { BootStatus, ConfigurationSnapshot, DesktopShellPreferences, GlobalConfigProvidersResponse, SpacesResponse, WorkspaceSnapshot } from "#neoastra";
import { desktopShell as demoShell, globalConfig as demoConfig, modelCatalog as demoModels, spaces as demoSpaces, workspace as demoWorkspace } from "./demo-api";
export * from "./demo-api";

const epoch = "12345678-1234-1234-1234-123456789abc";
const calls: string[] = [];
Object.assign(window, { startupLayoutFixture: { calls } });

export const boot = {
  status: async (): Promise<BootStatus> => {
    // Native RPC replies arrive after the mount, not in its microtask batch. Leave an initial
    // workspace frame before App receives its boot status and opens the provider settings.
    await new Promise(resolve => setTimeout(resolve, 25));
    return { state: "owned-text-only", hostAvailable: true, hostEpoch: epoch,
      commandReviewEnabled: false, ownedAsksEnabled: false, ownedUserInputEnabled: false,
      configRecovery: false, providerSetup: true, developerMode: false, productName: "CodeAlta", version: "fixture" };
  },
  appearance: async () => ({ status: "ok" }),
};
export const workspace = {
  ...demoWorkspace,
  snapshot: async (): Promise<WorkspaceSnapshot> => ({ configured: true, projects: [], sessions: [],
    projectsTruncated: false, sessionsTruncated: false, displayTextTruncated: false }),
  draftPrompts: async () => ({ status: "ok", hostEpoch: epoch, projectId: null, projectPath: null,
    prompts: [{ id: "default", name: "Default" }] }),
};
export const configuration = {
  snapshot: async (): Promise<ConfigurationSnapshot> => ({ providerRuntimeAvailable: true, pluginRuntimeAvailable: true,
    providerBrands: [], providers: [], plugins: [], providersTruncated: false, pluginsTruncated: false }),
};
export const modelCatalog = {
  ...demoModels,
  providers: async () => ({ status: "ok", epoch, providers: [], truncated: false }),
};
export const globalConfig = {
  ...demoConfig,
  providers: async (): Promise<GlobalConfigProvidersResponse> => {
    calls.push("providers");
    return { status: "ok", revision: "A".repeat(64), defaultProvider: null, startingProvider: null,
      providers: [], providerTypes: ["openai-chat"], reasoningEfforts: [], typeDefaults: [], builtIn: [], unsupported: [], permissionModes: [] };
  },
};
export const desktopShell = {
  ...demoShell,
  preferences: async (): Promise<DesktopShellPreferences> => ({ status: "ok", onClose: "ask", canKeepRunning: true,
    platform: "windows", trayIcon: true, entryAdded: false, zoom: 100, sessionWidth: 100, sessionWidths: [], reviewPermissions: true, inheritPermissions: false }),
};
export const spaces = {
  ...demoSpaces,
  list: async (): Promise<SpacesResponse> => ({ status: "ok", spaces: [{ id: "default", name: "Default", isDefault: true,
    description: null, icon: null, color: null, file: null, projectIds: [] }] }),
  activity: async () => ({ status: "ok", sessions: [], truncated: false }),
};
