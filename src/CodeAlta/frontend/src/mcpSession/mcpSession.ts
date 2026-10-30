/** An argument of an MCP tool. */
export type McpArgumentView = Readonly<{ name: string; description: string | null }>;

/** A tool of an MCP server as a session sees it: `active` when the session has it, `enabled` when the policy lets it be called. */
export type McpToolView = Readonly<{ name: string; alias: string; title: string | null; description: string | null;
  arguments: readonly McpArgumentView[]; enabled: boolean; active: boolean }>;

/**
 * An MCP server as a session sees it. `state` is `active` when the session uses it (activated as a whole or with a
 * tool), `available` when it could activate it, `disabled` when the policy turns it off.
 */
export type McpServerView = Readonly<{ key: string; transport: string; source: string; scope: string; required: boolean; activated: boolean;
  state: "active" | "available" | "disabled"; activeToolCount: number | null; listedAt: string | null; omittedTools: number; tools: readonly McpToolView[] }>;

/** What the MCP window shows: whether MCP is on, and the servers. */
export type McpSessionView = Readonly<{ enabled: boolean; omitted: number; servers: readonly McpServerView[] }>;

/** The counts the summary of the window shows. */
export type McpSessionSummary = Readonly<{ servers: number; activeServers: number; disabledServers: number; activeTools: number; tools: number }>;

const text = (value: unknown, maximum: number): value is string => typeof value === "string" && value.length <= maximum;
const optional = (value: unknown, maximum: number): string | null => text(value, maximum) && value.length > 0 ? value : null;
const count = (value: unknown): number => typeof value === "number" && Number.isSafeInteger(value) && value >= 0 ? value : 0;
const record = (value: unknown): Record<string, unknown> | null => value && typeof value === "object" && !Array.isArray(value) ? value as Record<string, unknown> : null;
const list = (value: unknown, maximum: number): readonly unknown[] => Array.isArray(value) ? value.slice(0, maximum) : [];

function tool(value: unknown): McpToolView | null {
  const entry = record(value);
  if (!entry || !text(entry.name, 160) || !entry.name || !text(entry.alias, 160)) return null;
  const args = list(entry.arguments, 32).flatMap(item => {
    const argument = record(item);
    return argument && text(argument.name, 160) && argument.name ? [{ name: argument.name, description: optional(argument.description, 160) }] : [];
  });
  const enabled = entry.enabled === true;
  return { name: entry.name, alias: entry.alias || entry.name, title: optional(entry.title, 160), description: optional(entry.description, 4000),
    arguments: args, enabled, active: enabled && entry.active === true };
}

function server(value: unknown): McpServerView | null {
  const entry = record(value);
  if (!entry || !text(entry.key, 160) || !entry.key) return null;
  const enabled = entry.enabled === true;
  // The policy turns off every tool of a server it turns off.
  const tools = list(entry.tools, 256).map(tool).filter((item): item is McpToolView => item !== null)
    .map(item => enabled ? item : { ...item, enabled: false, active: false });
  return { key: entry.key, transport: entry.transport === "http" ? "http" : "stdio", source: optional(entry.source, 160) ?? "",
    scope: entry.scope === "project" ? "project" : "global", required: entry.required === true, activated: enabled && entry.activated === true,
    state: !enabled ? "disabled" : entry.inUse === true ? "active" : "available",
    activeToolCount: typeof entry.activeToolCount === "number" ? count(entry.activeToolCount) : null,
    listedAt: optional(entry.listedAt, 64), omittedTools: count(entry.omittedTools), tools };
}

/**
 * Accepts a well-formed `composerStatus.mcp` answer for the asked project; a refusal or a malformed answer is null,
 * and a malformed server or tool is left out.
 */
export function mcpSessionView(reply: unknown, projectId: string | null): McpSessionView | null {
  const value = record(reply);
  if (!value || value.status !== "ok" || (value.projectId ?? null) !== projectId || !Array.isArray(value.servers)) return null;
  const servers = list(value.servers, 64).map(server).filter((item): item is McpServerView => item !== null);
  return { enabled: value.enabled === true, omitted: count(value.omitted), servers };
}

/** The tools of a server the session has: those it was given, else, before a run listed them, the active ones known. */
export function activeToolCount(server: McpServerView): number {
  if (server.state !== "active") return 0;
  return server.activeToolCount ?? server.tools.filter(item => item.active).length;
}

/** The counts of the summary: servers, those in use and turned off, and the tools the session has out of the known ones. */
export function mcpSessionSummary(view: McpSessionView): McpSessionSummary {
  return {
    servers: view.servers.length,
    activeServers: view.servers.filter(item => item.state === "active").length,
    disabledServers: view.servers.filter(item => item.state === "disabled").length,
    activeTools: view.servers.reduce((sum, item) => sum + activeToolCount(item), 0),
    tools: view.servers.reduce((sum, item) => sum + item.tools.length + item.omittedTools, 0),
  };
}

/** Which tools the window lists: those the session has, or every tool. */
export type McpToolScope = "active" | "all";

/**
 * The servers and tools a filter keeps, servers in use first. A query matches the key of a server (all its tools are
 * then kept) or the name, title or description of a tool; with the `active` scope only the servers in use and the
 * tools the session has are kept.
 */
export function filterMcpServers(servers: readonly McpServerView[], query: string, scope: McpToolScope): McpServerView[] {
  const words = query.toLocaleLowerCase().split(/\s+/).filter(Boolean);
  const matches = (...values: (string | null)[]) => {
    const haystack = values.filter(Boolean).join("\n").toLocaleLowerCase();
    return words.every(word => haystack.includes(word));
  };
  const order = { active: 0, available: 1, disabled: 2 } as const;
  return servers
    .filter(item => scope === "all" || item.state === "active")
    .flatMap(item => {
      const inScope = scope === "all" ? item.tools : item.tools.filter(entry => entry.active);
      if (words.length === 0 || matches(item.key)) return [{ ...item, tools: inScope }];
      const tools = inScope.filter(entry => matches(entry.name, entry.title, entry.description));
      return tools.length > 0 ? [{ ...item, tools }] : [];
    })
    .sort((left, right) => order[left.state] - order[right.state] || left.key.localeCompare(right.key));
}
