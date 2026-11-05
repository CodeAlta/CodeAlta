// What the settings page of the Mobile plugin reads and writes: the answers of its calls (`mobile.settings.get`, `set`, `reset`), and the
// settings a person types. The plugin publishes the view through a provider (Tailscale, Dev Tunnels…): each one has its own fields,
// listed here. Nothing here touches the page.

/** The lowest port the plugin accepts, and the highest. */
export const minimumPort = 1024, maximumPort = 65535;

/** A field of the settings some providers need, beside the port of this computer that all have. */
export type MobileProviderField = "httpsPort";

/** The fields of each provider. A provider the page does not know has the port of the network when the plugin says it takes one. */
const providerFields: Readonly<Record<string, readonly MobileProviderField[]>> = {
  tailscale: ["httpsPort"],
  devtunnel: [],
};

/** A private network phones can connect through, as the plugin offers it: Tailscale, Dev Tunnels, or one a plugin brings. */
export type MobileNetwork = Readonly<{ id: string; name: string; description: string; publicPort: boolean }>;

/** What the plugin says of its settings: the provider and its settings, the address while the view is served, and what keeps it from being served. */
export type MobileSettingsView = Readonly<{
  /** The networks the plugin offers, in their order. */
  providers: readonly MobileNetwork[];
  /** The provider of now, and what it does. */
  provider: Readonly<{ id: string; state: string; detail: string }>;
  port: number;
  /** The HTTPS port on the tailnet, for Tailscale. */
  httpsPort: number;
  /** The address of the view, without the access token; null while it is not served. */
  address: string | null;
  /** `starting`, `listening`, `port-in-use`, `failed` or `stopped`. */
  server: string;
  serverDetail: string | null;
  /** Why the settings that were asked for are refused; null otherwise. */
  error: string | null;
}>;

/** What a person chooses and types, until it is applied. */
export type MobileSettingsDraft = Readonly<{ provider: string; port: string; httpsPort: string }>;

/** What Apply sends to `mobile.settings.set`: the fields of the provider chosen, the others left as the plugin has them. */
export type MobileSettingsChange = Readonly<{ provider: string; port: number; httpsPort?: number }>;

const text = (value: unknown, limit: number): string | null => typeof value === "string" && value.length <= limit ? value : null;
const isPort = (value: unknown): value is number => typeof value === "number" && Number.isInteger(value) && value >= minimumPort && value <= maximumPort;

/** Reads an answer of the plugin; null when it is not one. */
export function readMobileSettings(value: unknown): MobileSettingsView | null {
  if (!value || typeof value !== "object") return null;
  const record = value as Record<string, unknown>;
  const provider = record.provider && typeof record.provider === "object" ? record.provider as Record<string, unknown> : null;
  const server = text(record.server, 64), id = text(provider?.id, 64), state = text(provider?.state, 64), detail = text(provider?.detail, 1000);
  if (!isPort(record.port) || !isPort(record.httpsPort) || !server || !id || !state || detail === null) return null;
  const providers: MobileNetwork[] = Array.isArray(record.providers) ? record.providers.flatMap(item => {
    if (typeof item === "string") return item.length > 0 && item.length <= 64 ? [{ id: item, name: item, description: "", publicPort: item === "tailscale" }] : [];
    if (!item || typeof item !== "object") return [];
    const network = item as Record<string, unknown>;
    const networkId = text(network.id, 64), name = text(network.name, 80);
    return networkId && name ? [{ id: networkId, name, description: text(network.description, 300) ?? "", publicPort: network.publicPort === true }] : [];
  }) : [];
  const address = text(record.address, 512);
  return {
    providers: providers.length > 0 ? providers : [{ id, name: id, description: "", publicPort: false }], provider: { id, state, detail },
    port: record.port, httpsPort: record.httpsPort,
    address: address && /^https:\/\//u.test(address) ? address : null, server, serverDetail: text(record.serverDetail, 600) || null,
    error: text(record.error, 600) || null,
  };
}

/** The port a text says, or null when it is not a whole number from 1024 to 65535. */
export function readPort(value: string): number | null {
  const trimmed = value.trim();
  if (!/^\d{1,5}$/u.test(trimmed)) return null;
  const port = Number(trimmed);
  return isPort(port) ? port : null;
}

/** The fields as the plugin's settings fill them, before anything is chosen or typed. */
export function draftOf(settings: MobileSettingsView): MobileSettingsDraft {
  return { provider: settings.provider.id, port: String(settings.port), httpsPort: String(settings.httpsPort) };
}

/** The fields of a provider: those the page knows, or the port of the network for a network of a plugin that takes one. */
export function fieldsOf(provider: string, networks: readonly MobileNetwork[] = []): readonly MobileProviderField[] {
  return providerFields[provider] ?? (networks.find(network => network.id === provider)?.publicPort ? ["httpsPort"] : []);
}

/**
 * What Apply sends: the provider chosen with its fields, when the ports it shows are ports and something is not what the plugin uses. Null
 * otherwise.
 */
export function changedSettings(settings: MobileSettingsView, draft: MobileSettingsDraft): MobileSettingsChange | null {
  const fields = fieldsOf(draft.provider, settings.providers);
  const port = readPort(draft.port);
  const httpsPort = fields.includes("httpsPort") ? readPort(draft.httpsPort) : settings.httpsPort;
  if (port === null || httpsPort === null) return null;
  const same = draft.provider === settings.provider.id && port === settings.port && httpsPort === settings.httpsPort;
  if (same) return null;
  return {
    provider: draft.provider, port,
    ...(fields.includes("httpsPort") ? { httpsPort } : {}),
  };
}
