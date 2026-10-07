/**
 * The MCP server of the application, as its settings page shows it: the address other applications connect to,
 * and the configuration they are given.
 */
import type { McpHostResponse } from "#neoastra";

/** What the host says about its MCP server: `state` is `running`, `stopped` (turned off) or `failed` (turned on, and not listening). */
export type McpHostState = Readonly<McpHostResponse>;

/** The name clients give the server: the developer instance runs beside the normal one, under another name. */
export const mcpServerName = (developer: boolean) => developer ? "codealta-dev" : "codealta";

/**
 * The entry a client's MCP configuration takes for this server, in the `mcpServers` shape most clients read:
 * the transport, the address and, when the server asks for one, the access token as a bearer token.
 */
export function mcpClientConfiguration(name: string, url: string, token: string | null): string {
  const server: Record<string, unknown> = { type: "http", url };
  if (token) server.headers = { Authorization: `Bearer ${token}` };
  return JSON.stringify({ mcpServers: { [name]: server } }, null, 2);
}
