// Isolated bridge for mounting the actual main.tsx with spaces: the catalog of the project fixture, and a host
// that keeps two spaces in memory. No native bridge or user data is touched.
import type { SpaceAssignRequest, SpaceIdentityCheckRequest, SpaceIdentityItem, SpaceIdentityUpdateRequest, SpaceItem, SpaceShownRequest, SpacesEvent } from "#neoastra";
export * from "../projectFocus.neoastra.mount";
import { mcpServers as fixtureServers } from "../projectFocus.neoastra.mount";
// The real editor can open an in-memory file; disk and git operations remain unavailable.
export { projectFiles, projectGit } from "../demo-api";
import { globalConfig as demoConfig } from "../demo-api";

const space = (id: string, name: string, projectIds: string[], look: Partial<SpaceItem> = {}): SpaceItem =>
  ({ id, name, description: null, icon: null, color: null, isDefault: id === "default", projectIds, file: id === "default" ? null : `/fixture/spaces/${id}.md`, agentPrompt: null, disabledTools: [], ...look });
// The default space holds both projects of the fixture; Work has the second one.
const listed: SpaceItem[] = [space("default", "Default", ["project", "other"]), space("work", "Work",
  localStorage.getItem("settingsFixtureEmptySpace") === "true" ? [] : ["other"], { icon: "briefcase", color: "#2d72d2" })];
const fixture: { shown: string[]; assigned: SpaceAssignRequest[]; tell: (event: SpacesEvent) => void; sessions: object[]; serverReads: number; providerReads: number } =
  { shown: [], assigned: [], tell: () => { }, sessions: [], serverReads: 0, providerReads: 0 };
Object.assign(window, { spacesFixture: fixture });
// How many times the page of a space read the MCP servers: once when it opens, not at each render.
export const mcpServers = { ...fixtureServers, list: (...args: Parameters<typeof fixtureServers.list>) => { fixture.serverReads++; return fixtureServers.list(...args); } };
const done = async () => ({ status: "ok", message: null, space: null });

// The providers of the user: an account of Work, one of the default space, and one of every space.
const account = (key: string, name: string) => ({ key, type: "claude-code", enabled: true, displayName: name, effectiveName: name, model: null, reasoningEffort: null,
  apiUrl: null, effectiveApiUrl: null, apiKeyEnv: null, hasApiKey: false, defaults: { displayName: name, model: null, reasoningEffort: null, apiUrl: null, apiKeyEnv: null },
  icon: null, color: null, anthropicApiKey: null, permissionMode: null, promptSuggestions: false, configDir: null, command: null });
export const globalConfig = { ...demoConfig, providers: async () => (fixture.providerReads++, { status: "ok", revision: "revision", defaultProvider: "claude-business", startingProvider: "claude-business",
  providers: [account("claude-business", "Claude Business"), account("claude-home", "Claude Home"), account("codex", "Codex")],
  providerTypes: ["claude-code"], reasoningEfforts: [], permissionModes: [], typeDefaults: [], builtIn: [], unsupported: [] }) };

// The identity of the spaces, kept in memory: what the page saved and checked is recorded for the test.
const identityFixture: { identity: SpaceIdentityItem | null; updates: SpaceIdentityUpdateRequest[]; checks: SpaceIdentityCheckRequest[] } = { identity: null, updates: [], checks: [] };
Object.assign(window, { identityFixture });
export const spaceIdentity = {
  get: async () => ({ status: "ok", message: null, identity: identityFixture.identity, usual: { gitName: "Usual Name", gitEmail: "usual@example.com", signingKey: null,
    signingFormat: null, gitHubFolder: "C:\\Users\\me\\AppData\\Roaming\\GitHub CLI", jiraEmail: null, jiraTokenVariable: "JIRA_API_TOKEN", home: "C:\\Users\\me", platform: "windows" } }),
  update: async (request: SpaceIdentityUpdateRequest) => {
    identityFixture.updates.push(request);
    const known = identityFixture.identity ?? { git: null, gitHub: null, jira: null };
    identityFixture.identity = { ...known, ...(request.part === "git" ? { git: request.git } : request.part === "github" ? { gitHub: request.gitHub } : { jira: request.jira }) };
    return { status: "ok", message: null, identity: identityFixture.identity, usual: null };
  },
  check: async (request: SpaceIdentityCheckRequest) => {
    identityFixture.checks.push(request);
    return { status: "ok", lines: [{ kind: "ok", text: `Commits as A. Mutel <${request.git?.email ?? "usual@example.com"}>` }] };
  },
  session: async () => ({ status: "unavailable", spaceId: null, gitAuthor: null, signing: null, signOff: false, gitHubProfile: null, jiraSite: null, jiraEmail: null }),
};

export const spaces = {
  list: async () => ({ status: "ok", spaces: listed }),
  activity: async () => ({ status: "ok", sessions: fixture.sessions, truncated: false }),
  create: done, update: done, delete: done, reorder: done,
  assign: async (request: SpaceAssignRequest) => {
    fixture.assigned.push(request);
    for (const item of listed) {
      const index = listed.indexOf(item);
      const joins = request.join?.includes(item.id), leaves = request.leave?.includes(item.id);
      if (joins || leaves) listed[index] = { ...item, projectIds: [...item.projectIds.filter(id => id !== request.projectId), ...(joins ? [request.projectId!] : [])] };
    }
    return done();
  },
  shown: async (request: SpaceShownRequest) => { fixture.shown.push(request.id ?? ""); return done(); },
  assignments: async () => ({ status: "ok", providers: [{ key: "claude-business", spaces: ["work"] }, { key: "claude-home", spaces: ["default"] }],
    mcpServers: [], plugins: [], skills: [], prompts: [] }),
  assignResource: done, setAgentPrompt: done, setDisabledTools: done,
  sessionResources: async () => ({ status: "unavailable", spaceId: null, mcpServers: [], skills: [], prompts: [], plugins: [] }),
  watch: async (_request: object, options?: { signal?: AbortSignal }) => {
    const waiting: SpacesEvent[] = [];
    let wake = () => { };
    fixture.tell = event => { waiting.push(event); wake(); };
    options?.signal?.addEventListener("abort", () => wake(), { once: true });
    return (async function* () {
      while (!options?.signal?.aborted) {
        if (waiting.length === 0) await new Promise<void>(resolve => { wake = resolve; });
        while (waiting.length > 0) yield waiting.shift()!;
      }
    })();
  },
};
