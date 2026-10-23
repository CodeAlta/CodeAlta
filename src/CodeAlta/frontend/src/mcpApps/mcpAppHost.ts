import { createContext } from "react";
import { McpAppError, type McpAppHostContext } from "./mcpAppBridge";

/** An open view, as the host returns it: its frame's address and token, and what its tool call was. */
export type McpAppOpened = Readonly<{
  token: string; frameUrl: string; server: string; tool: string; prefersBorder?: boolean | null;
  toolInput: string; toolResult: string; toolDefinition?: string | null;
}>;

type Answer = Readonly<{ status: string; json?: string | null; code?: string | null; error?: string | null }>;

/** The requests of the views of the host (the `mcpApps` service), injected for tests. */
export type McpAppsApi = Readonly<{
  open(request: { expectedEpoch: string; sessionId: string; offset: string }): Promise<Readonly<{ status: string; view?: McpAppOpened | null; code?: string | null; error?: string | null }>>;
  callTool(request: { expectedEpoch: string; token: string; name: string; argumentsJson: string | null }): Promise<Answer>;
  readResource(request: { expectedEpoch: string; token: string; uri: string }): Promise<Answer>;
  close(request: { expectedEpoch: string; token: string }): Promise<unknown>;
}>;

export type McpAppOpenResult = { view: McpAppOpened; error?: undefined } | { view?: undefined; error: string };

/** What the views of a session pane share: the host of their requests, and the views that are live. */
export type McpAppsHost = Readonly<{
  /** When the pane started: a call that ended after it shows its view at once, an older one on demand. */
  startedAt: number;
  hostVersion: string;
  open(offset: string): Promise<McpAppOpenResult>;
  callTool(token: string, name: string, args: Record<string, unknown>): Promise<unknown>;
  readResource(token: string, uri: string): Promise<unknown>;
  close(token: string): void;
  sendMessage(text: string): Promise<boolean>;
  openLink(url: string): boolean;
  live: McpAppLiveViews;
}>;

/** The views of a session pane with a live frame; one more than the limit sends the oldest back to its button. */
export type McpAppLiveViews = Readonly<{ claim(key: string, evict: () => void): void; release(key: string): void; count(): number }>;

/** The most views of a pane with a live frame. */
export const maximumLiveViews = 4;

export const McpAppsContext = createContext<McpAppsHost | null>(null);

export function createLiveViews(limit = maximumLiveViews): McpAppLiveViews {
  const views = new Map<string, () => void>();
  return {
    claim(key, evict) {
      views.delete(key);
      views.set(key, evict);
      while (views.size > limit) {
        const [oldest, leave] = views.entries().next().value!;
        views.delete(oldest);
        leave();
      }
    },
    release(key) { views.delete(key); },
    count: () => views.size,
  };
}

/** The host of the views of one session of an owned host. */
export function createMcpAppsHost(api: McpAppsApi, epoch: string, sessionId: string, sendMessage: (text: string) => Promise<boolean>,
  openLink: (url: string) => boolean, hostVersion: string, startedAt = Date.now()): McpAppsHost {
  const answer = async (reply: Promise<Answer>) => {
    const result = await reply;
    if (result.status === "ok" && typeof result.json === "string") return JSON.parse(result.json) as unknown;
    throw new McpAppError(-32000, result.error || result.code || result.status);
  };
  return {
    startedAt, hostVersion, sendMessage, live: createLiveViews(),
    async open(offset) {
      const reply = await api.open({ expectedEpoch: epoch, sessionId, offset });
      return reply.status === "ok" && reply.view ? { view: reply.view } : { error: reply.error || reply.code || reply.status };
    },
    callTool: (token, name, args) => answer(api.callTool({ expectedEpoch: epoch, token, name, argumentsJson: JSON.stringify(args) })),
    readResource: (token, uri) => answer(api.readResource({ expectedEpoch: epoch, token, uri })),
    close(token) { void api.close({ expectedEpoch: epoch, token }).catch(() => undefined); },
    // Only an address of the web or of mail leaves the window, through the opener of its links.
    openLink: url => /^(https?:|mailto:)/iu.test(url) && url.length <= 4096 && openLink(url),
  };
}

// The variables of MCP Apps that the theme of the window gives, from its own (style.css): a view drawn with them looks
// like the window, in its scheme of colors.
const styleVariables: readonly (readonly [string, string])[] = [
  ["--color-background-primary", "--panel"], ["--color-background-secondary", "--panel-2"], ["--color-background-tertiary", "--panel-3"],
  ["--color-background-inverse", "--text"], ["--color-background-info", "--accent-soft"],
  ["--color-text-primary", "--text"], ["--color-text-secondary", "--muted"], ["--color-text-tertiary", "--muted"], ["--color-text-inverse", "--panel"],
  ["--color-text-info", "--accent"], ["--color-text-danger", "--red"], ["--color-text-success", "--green"], ["--color-text-warning", "--yellow"],
  ["--color-border-primary", "--line"], ["--color-border-secondary", "--line-strong"], ["--color-border-tertiary", "--line"],
  ["--color-border-info", "--accent"], ["--color-border-danger", "--red"], ["--color-border-success", "--green"], ["--color-border-warning", "--yellow"],
  ["--color-ring-primary", "--accent"],
  ["--font-sans", "--bp-typography-family-default"], ["--font-mono", "--code-font"],
];

/** The theme and the variables of the window now, as a view is told of them. */
export function hostStyles(root: HTMLElement = document.documentElement): Pick<McpAppHostContext, "theme" | "styles"> {
  const computed = getComputedStyle(root);
  const variables: Record<string, string> = {};
  for (const [name, source] of styleVariables) {
    const value = computed.getPropertyValue(source).trim();
    if (value) variables[name] = value;
  }
  return { theme: root.dataset.theme === "light" ? "light" : "dark", styles: { variables } };
}
