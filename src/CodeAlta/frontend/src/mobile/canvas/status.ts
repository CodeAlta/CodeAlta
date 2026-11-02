// What the Mobile tab shows, from the status the Mobile plugin gives (`mobile.status`, `mobile.retry`, `mobile.act`, the event
// `mobile.changed`): the QR code of the link, and what keeps a phone from connecting, with what fixes it. The plugin publishes the view
// through a provider (Tailscale, Dev Tunnels…): each provider has its table of the words and the buttons of its states. Plain functions,
// without the DOM.

/** The state of the plugin's server on this computer. */
export type MobileServerState = "starting" | "listening" | "port-in-use" | "failed" | "stopped";

/** What the provider of the connection does, as the plugin says it: its state is one of the provider's own. */
export type MobileProviderStatus = Readonly<{
  /** The id of the provider: `tailscale`, `devtunnel`… */
  id: string;
  /** `pending`, `serving`, `stopped`, or a state of the provider that says what keeps phones out. */
  state: string;
  /** The address phones open, while it is served. */
  url: string | null;
  /** The plugin's line, for people. */
  detail: string;
  /** The page that fixes it, when there is one. */
  fixUrl: string | null;
}>;

/** The status of the Mobile plugin. */
export type MobileStatus = Readonly<{
  server: MobileServerState;
  /** The port of the server on this computer. */
  port: number;
  /** Why the server failed, for people. */
  serverDetail: string | null;
  provider: MobileProviderStatus;
  /** What the QR code holds: `<url>/#k=<token>`; null while nothing can be reached. */
  connectUrl: string | null;
  /** The same without the token, shown under the QR code. */
  address: string | null;
  /** The QR code of `connectUrl`: one string of '0' and '1' per row, square. */
  qr: readonly string[] | null;
}>;

const serverStates: readonly MobileServerState[] = ["starting", "listening", "port-in-use", "failed", "stopped"];
const isRecord = (value: unknown): value is Record<string, unknown> => typeof value === "object" && value !== null && !Array.isArray(value);
const text = (value: unknown): string | null => typeof value === "string" && value.length > 0 ? value : null;

/** Reads a status as the plugin sent it; null when it is not one. A QR code that is not a square of 0 and 1 is left out. */
export function readStatus(value: unknown): MobileStatus | null {
  if (!isRecord(value) || !isRecord(value.provider)) return null;
  const server = serverStates.find(state => state === value.server);
  const provider = value.provider;
  const id = text(provider.id), state = text(provider.state);
  if (!server || !id || !state) return null;
  const rows = Array.isArray(value.qr) && value.qr.length > 0 && value.qr.every(row => typeof row === "string" && row.length === (value.qr as unknown[]).length && /^[01]+$/.test(row))
    ? value.qr as string[] : null;
  return {
    server, port: typeof value.port === "number" ? value.port : 0, serverDetail: text(value.serverDetail),
    provider: { id, state, url: text(provider.url), detail: text(provider.detail) ?? "", fixUrl: text(provider.fixUrl) },
    connectUrl: text(value.connectUrl), address: text(value.address), qr: rows,
  };
}

/** The quiet zone of a QR code, in modules: what the camera of a phone needs around it. */
export const quietZone = 4;

/**
 * The QR code of the rows as one SVG path of its dark modules, in a square of `size` modules with the quiet zone around it. A run of dark
 * modules in a row is one rectangle, so the path stays short and its edges meet exactly.
 */
export function qrPath(rows: readonly string[]): { size: number; path: string } {
  const size = rows.length + 2 * quietZone;
  let path = "";
  rows.forEach((row, y) => {
    for (let x = 0; x < row.length;) {
      if (row[x] !== "1") { x++; continue; }
      let end = x;
      while (end < row.length && row[end] === "1") end++;
      path += `M${x + quietZone} ${y + quietZone}h${end - x}v1h-${end - x}z`;
      x = end;
    }
  });
  return { size, path };
}

/** A sentence of the tab, by its English text (the key of its translations, `messages.ts`). */
export type MobileText = "The phone page is starting." | "Tailscale is starting to publish the page."
  | "The port {port} is in use by another program." | "Another CodeAlta with the Mobile plugin on may be using it."
  | "The server of the phone page did not start." | "The server of the phone page is stopped."
  | "Tailscale is not installed on this computer." | "Tailscale is not running on this computer."
  | "Tailscale Serve is not enabled for this tailnet." | "The port of Tailscale Serve is used by another service."
  | "Tailscale could not publish the page." | "The page is not published on the tailnet."
  | "Download Tailscale" | "Enable it in the admin console" | "Open Tailscale" | "Open the page that fixes it"
  | "Scan it with the camera of the phone. The phone must be signed in to the same tailnet."
  | "Scan it with the camera of the phone. The first time, the phone shows a page of Microsoft: choose Continue."
  | "Scan it with the camera of the phone."
  | "Dev Tunnels is opening the tunnel." | "Waiting for the sign-in in the browser…"
  | "The Dev Tunnels command line is not installed." | "Sign in to Dev Tunnels." | "The Dev Tunnel did not open." | "The Dev Tunnel is closed."
  | "Install Dev Tunnels" | "Sign in with Microsoft" | "Sign in with GitHub" | "The phone page could not be published.";

/** A button of a problem that runs an action of the provider (`mobile.act`). */
export type MobileProviderAction = Readonly<{ action: string; label: MobileText; primary?: boolean }>;

/** Something that keeps a phone from connecting: what it is, why (the plugin's own words), and what fixes it. */
export type MobileProblem = Readonly<{
  key: "server" | "provider";
  /** Danger when nothing can work until it is fixed; warning otherwise. */
  intent: "danger" | "warning";
  title: MobileText;
  parameters?: Readonly<Record<string, string | number>>;
  /** The plugin's line, when it says more than the title. */
  detail: string | null;
  /** What the tab says when the plugin gives no line. */
  hint?: MobileText;
  /** A button that opens the page that fixes it. */
  fix: Readonly<{ label: MobileText; url: string }> | null;
  /** Whether "Try again" asks the provider again: the states that can change once fixed. */
  retry: boolean;
  /** The buttons that run actions of the provider (a sign-in). */
  actions: readonly MobileProviderAction[];
}>;

/** What the tab shows: the code when there is one, what keeps a phone from connecting, and what is still starting. */
export type MobileView = Readonly<{
  code: Readonly<{ size: number; path: string; address: string }> | null;
  problems: readonly MobileProblem[];
  /** A step that is not done yet and is nobody's fault, while there is no code. */
  waiting: MobileText | null;
  /** The line under the code. */
  instruction: MobileText;
}>;

/** How the tab speaks of a state of a provider that keeps phones out. */
type ProviderProblem = Readonly<{
  title: MobileText;
  danger?: boolean;
  /** The label of the button of `fixUrl`. */
  fix?: MobileText;
  retry?: boolean;
  actions?: readonly MobileProviderAction[];
  /** Whether the title says it all: the plugin's line is not shown. */
  quiet?: boolean;
}>;

/** How the tab speaks of a provider: its problems by state, its steps that run, and the line under the code. */
type ProviderText = Readonly<{
  problems: Readonly<Record<string, ProviderProblem>>;
  waiting: Readonly<Record<string, MobileText>>;
  instruction: MobileText;
}>;

/** The words and the buttons of each provider. A provider the tab does not know shows the plugin's line. */
const providers: Readonly<Record<string, ProviderText>> = {
  tailscale: {
    problems: {
      "not-installed": { title: "Tailscale is not installed on this computer.", danger: true, fix: "Download Tailscale" },
      "not-running": { title: "Tailscale is not running on this computer.", fix: "Open Tailscale", retry: true },
      "serve-disabled": { title: "Tailscale Serve is not enabled for this tailnet.", danger: true, fix: "Enable it in the admin console", retry: true },
      "port-taken": { title: "The port of Tailscale Serve is used by another service.", retry: true },
      "failed": { title: "Tailscale could not publish the page.", retry: true },
      "stopped": { title: "The page is not published on the tailnet." },
    },
    waiting: { pending: "Tailscale is starting to publish the page." },
    instruction: "Scan it with the camera of the phone. The phone must be signed in to the same tailnet.",
  },
  devtunnel: {
    problems: {
      "not-installed": { title: "The Dev Tunnels command line is not installed.", danger: true, fix: "Install Dev Tunnels", retry: true },
      "not-signed-in": {
        title: "Sign in to Dev Tunnels.", quiet: true,
        actions: [{ action: "signin-microsoft", label: "Sign in with Microsoft", primary: true }, { action: "signin-github", label: "Sign in with GitHub" }],
      },
      "failed": { title: "The Dev Tunnel did not open.", retry: true },
      "stopped": { title: "The Dev Tunnel is closed." },
    },
    waiting: { pending: "Dev Tunnels is opening the tunnel.", "signing-in": "Waiting for the sign-in in the browser…" },
    instruction: "Scan it with the camera of the phone. The first time, the phone shows a page of Microsoft: choose Continue.",
  },
};

// A state of a provider the tab has no words for: the plugin's line under a general title. The steps that run are no problem.
const unknownProblem: ProviderProblem = { title: "The phone page could not be published.", retry: true };
const steps: ReadonlySet<string> = new Set(["pending", "serving", "signing-in"]);

/** What the tab shows for a status. */
export function mobileView(status: MobileStatus): MobileView {
  const problems: MobileProblem[] = [];
  const server = status.server;
  if (server === "port-in-use") {
    problems.push({ key: "server", intent: "danger", title: "The port {port} is in use by another program.", parameters: { port: status.port },
      detail: status.serverDetail, hint: "Another CodeAlta with the Mobile plugin on may be using it.", fix: null, retry: false, actions: [] });
  } else if (server === "failed" || server === "stopped") {
    problems.push({ key: "server", intent: "danger", title: server === "failed" ? "The server of the phone page did not start." : "The server of the phone page is stopped.",
      detail: status.serverDetail, fix: null, retry: false, actions: [] });
  }
  const provider = status.provider;
  const words: ProviderText | undefined = providers[provider.id];
  const problem = steps.has(provider.state) ? undefined : words?.problems[provider.state] ?? (provider.state === "stopped" ? undefined : unknownProblem);
  if (problem) {
    problems.push({
      key: "provider", intent: problem.danger ? "danger" : "warning", title: problem.title,
      detail: problem.quiet ? null : provider.detail.trim() || null,
      fix: provider.fixUrl ? { label: problem.fix ?? "Open the page that fixes it", url: provider.fixUrl } : null,
      retry: problem.retry ?? false, actions: problem.actions ?? [],
    });
  }
  const code = status.connectUrl && status.qr ? { ...qrPath(status.qr), address: status.address ?? status.connectUrl.replace(/#.*$/u, "") } : null;
  const waiting: MobileText | null = code || problems.length > 0 ? null
    : server === "starting" ? "The phone page is starting."
    : words?.waiting[provider.state] ?? (provider.state === "pending" ? "The phone page is starting." : null);
  return { code, problems, waiting, instruction: words?.instruction ?? "Scan it with the camera of the phone." };
}
