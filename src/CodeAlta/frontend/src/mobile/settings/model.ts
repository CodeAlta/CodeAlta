// What the settings page of the Mobile plugin reads and writes: the answers of its calls (`mobile.settings.get`, `set`, `reset`), and the
// ports a person types. Nothing here touches the page.

/** The lowest port the plugin accepts, and the highest. */
export const minimumPort = 1024, maximumPort = 65535;

/** What the plugin says of its settings: the two ports, the address while the view is served, and what keeps it from being served. */
export type MobileSettingsView = Readonly<{
  port: number; httpsPort: number;
  /** The address of the view on the tailnet, without the access token; null while it is not served. */
  address: string | null;
  /** `starting`, `listening`, `port-in-use`, `failed` or `stopped`. */
  server: string;
  serverDetail: string | null;
  tailscale: Readonly<{ state: string; detail: string }>;
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
  return {
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

/** The ports typed, when both are ports and one of them is not the one the plugin uses: what Apply sends. Null otherwise. */
export function changedPorts(settings: Pick<MobileSettingsView, "port" | "httpsPort">, port: string, httpsPort: string): Readonly<{ port: number; httpsPort: number }> | null {
  const local = readPort(port), tailnet = readPort(httpsPort);
  if (local === null || tailnet === null || local === settings.port && tailnet === settings.httpsPort) return null;
  return { port: local, httpsPort: tailnet };
}
