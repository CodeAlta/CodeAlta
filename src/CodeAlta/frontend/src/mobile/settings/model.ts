// What the settings page of the Mobile plugin reads and writes: the answers of its calls (`mobile.settings.get`, `set`, `reset`), and the
// ports a person types. Nothing here touches the page.

/** The lowest port the plugin accepts, and the highest. */
export const minimumPort = 1024, maximumPort = 65535;

/** How phones connect: through Tailscale, or through a Dev Tunnel of Microsoft. */
export type MobileMode = "tailscale" | "devtunnel";

/** What the plugin says of its settings: the two ports, the address while the view is served, and what keeps it from being served. */
export type MobileSettingsView = Readonly<{
  mode: MobileMode;
  port: number; httpsPort: number;
  /** The address of the view on the tailnet, without the access token; null while it is not served. */
  address: string | null;
  /** `starting`, `listening`, `port-in-use`, `failed` or `stopped`. */
  server: string;
  serverDetail: string | null;
  tailscale: Readonly<{ state: string; detail: string }>;
  devTunnel: Readonly<{ state: string; detail: string }>;
  /** Why the ports that were asked for are refused; null otherwise. */
  error: string | null;
}>;

const text = (value: unknown, limit: number): string | null => typeof value === "string" && value.length <= limit ? value : null;
const isPort = (value: unknown): value is number => typeof value === "number" && Number.isInteger(value) && value >= minimumPort && value <= maximumPort;

/** Reads an answer of the plugin; null when it is not one. */
export function readMobileSettings(value: unknown): MobileSettingsView | null {
  if (!value || typeof value !== "object") return null;
  const record = value as Record<string, unknown>;
  const tailscale = record.tailscale && typeof record.tailscale === "object" ? record.tailscale as Record<string, unknown> : null;
  const server = text(record.server, 64), state = text(tailscale?.state, 64), detail = text(tailscale?.detail, 600);
  if (!isPort(record.port) || !isPort(record.httpsPort) || !server || !state || detail === null) return null;
  const address = text(record.address, 512);
  const tunnel = record.devTunnel && typeof record.devTunnel === "object" ? record.devTunnel as Record<string, unknown> : {};
  return {
    mode: record.mode === "devtunnel" ? "devtunnel" : "tailscale",
    devTunnel: { state: text(tunnel.state, 64) ?? "unused", detail: text(tunnel.detail, 600) ?? "" },
    port: record.port, httpsPort: record.httpsPort, address: address && /^https:\/\//u.test(address) ? address : null, server, serverDetail: text(record.serverDetail, 600) || null,
    tailscale: { state, detail }, error: text(record.error, 600) || null,
  };
}

/** The port a text says, or null when it is not a whole number from 1024 to 65535. */
export function readPort(value: string): number | null {
  const trimmed = value.trim();
  if (!/^\d{1,5}$/u.test(trimmed)) return null;
  const port = Number(trimmed);
  return isPort(port) ? port : null;
}

/** What Apply sends to `mobile.settings.set`. */
export type MobileSettingsChange = Readonly<{ mode: MobileMode; port: number; httpsPort: number }>;

/**
 * The mode chosen and the ports typed, when both are ports and something is not what the plugin uses: what Apply sends. Null otherwise.
 * Through Dev Tunnels the port of the tailnet is not shown: what the plugin has is sent back.
 */
export function changedSettings(settings: Pick<MobileSettingsView, "mode" | "port" | "httpsPort">, mode: MobileMode, port: string, httpsPort: string): MobileSettingsChange | null {
  const local = readPort(port), tailnet = mode === "devtunnel" ? settings.httpsPort : readPort(httpsPort);
  if (local === null || tailnet === null || mode === settings.mode && local === settings.port && tailnet === settings.httpsPort) return null;
  return { mode, port: local, httpsPort: tailnet };
}
