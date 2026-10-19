// Isolated bridge for mounting the actual main.tsx with spaces: the catalog of the project fixture, and a host
// that keeps two spaces in memory. No native bridge or user data is touched.
import type { SpaceAssignRequest, SpaceCreateRequest, SpaceIdentityCheckRequest, SpaceIdentityItem, SpaceIdentityUpdateRequest, SpaceItem, SpaceShownRequest, SpacesEvent } from "#neoastra";
export * from "../projectFocus.neoastra.mount";
import { mcpServers as fixtureServers, skills as fixtureSkills } from "../projectFocus.neoastra.mount";
// The real editor can open an in-memory file; disk and git operations remain unavailable.
export { projectFiles, projectGit } from "../demo-api";
import { globalConfig as demoConfig } from "../demo-api";

const space = (id: string, name: string, projectIds: string[], look: Partial<SpaceItem> = {}): SpaceItem =>
  ({ id, name, description: null, icon: null, color: null, isDefault: id === "default", projectIds, file: id === "default" ? null : `/fixture/spaces/${id}.md`, agentPrompt: null, disabledTools: [], ownTiers: false, identityEmail: null, identityParts: [], ...look });
// The default space holds both projects of the fixture; Work has the second one.
const listed: SpaceItem[] = [space("default", "Default", ["project", "other"]), space("work", "Work",
  localStorage.getItem("settingsFixtureEmptySpace") === "true" ? [] : ["other"], { icon: "briefcase", color: "#2d72d2" }),
  // A second space without projects, for the drags from a card to another.
  ...localStorage.getItem("settingsFixtureThirdSpace") === "true" ? [space("home", "Home", [])] : []];
const fixture: { shown: string[]; assigned: SpaceAssignRequest[]; created: SpaceCreateRequest[]; tell: (event: SpacesEvent) => void; sessions: object[]; serverReads: number; providerReads: number;
  assignmentReads: number } = { shown: [], assigned: [], created: [], tell: () => { }, sessions: [], serverReads: 0, providerReads: 0, assignmentReads: 0 };
Object.assign(window, { spacesFixture: fixture });
// How many times the page of a space read the MCP servers: once when it opens, not at each render.
// One MCP server of the user, in every space: its page says so in the same words as the page of a space.
export const mcpServers = { ...fixtureServers, list: async (..._args: Parameters<typeof fixtureServers.list>) => (fixture.serverReads++, {
  status: "ok", projectId: null, globalConfigState: null, projectConfigState: null, mcpEnabled: true, policyReadError: false, omitted: 0, unsupported: [],
  servers: [{ key: "docs", scope: "Global", origin: "global", source: "mcp.json", transport: "Stdio", command: "npx", arguments: ["docs-mcp"], argumentsRedacted: false,
    workingDirectory: null, url: null, urlRedacted: false, environment: [], headers: [], enabled: true, disabledTools: [], overridesGlobal: false, shadowed: false,
    authorized: false, authorizationExpiresAt: null }] }) };
const done = async () => ({ status: "ok", message: null, space: null });
// The user has no skill yet: the page of a space points to the page that adds one.
export const skills = { ...fixtureSkills, list: async () => ({ status: "ok", projectId: null, skills: [], omitted: 0 }) };

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
    const known = identityFixture.identity ?? { git: null, gitHub: null, jira: null, cli: null };
    identityFixture.identity = { ...known, ...(request.part === "git" ? { git: request.git } : request.part === "github" ? { gitHub: request.gitHub }
      : request.part === "cli" ? { cli: request.cli } : { jira: request.jira }) };
    return { status: "ok", message: null, identity: identityFixture.identity, usual: null };
  },
  check: async (request: SpaceIdentityCheckRequest) => {
    identityFixture.checks.push(request);
    return { status: "ok", lines: [{ kind: "ok", text: request.part === "cli" ? `Checked ${request.profile?.name}` : `Commits as A. Mutel <${request.git?.email ?? "usual@example.com"}>` }] };
  },
  session: async () => ({ status: "unavailable", spaceId: null, gitAuthor: null, signing: null, signOff: false, gitHubProfile: null, jiraSite: null, jiraEmail: null, cli: [] }),
};

export const spaces = {
  list: async () => ({ status: "ok", spaces: listed }),
  activity: async () => ({ status: "ok", sessions: fixture.sessions, truncated: false }),
  // A space made from the window is kept with what it was asked to start from.
  create: async (request: SpaceCreateRequest) => {
    fixture.created.push(request);
    const made = space((request.name ?? "space").toLowerCase(), request.name ?? "Space", [...request.projectIds ?? []]);
    listed.push(made);
    return { status: "ok", message: null, space: made };
  },
  update: done, delete: done, reorder: done,
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
  assignments: async () => (fixture.assignmentReads++, { status: "ok", providers: [{ key: "claude-business", spaces: ["work"] }, { key: "claude-home", spaces: ["default"] }],
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
