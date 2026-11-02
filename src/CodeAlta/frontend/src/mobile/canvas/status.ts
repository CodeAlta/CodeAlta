// What the Mobile tab shows, from the status the Mobile plugin gives (`mobile.status`, `mobile.retry`, the event `mobile.changed`):
// the QR code of the link, and what keeps a phone from connecting, with what fixes it. Plain functions, without the DOM.

/** How phones connect: through Tailscale, or through a Dev Tunnel of Microsoft. */
export type MobileMode = "tailscale" | "devtunnel";

/** The state of the plugin's server on 127.0.0.1. */
export type MobileServerState = "starting" | "listening" | "port-in-use" | "failed" | "stopped";

/** The state of the page's publication on the tailnet, with `tailscale serve`. */
export type MobileTailscaleState = "pending" | "not-installed" | "not-running" | "serve-disabled" | "port-taken" | "serving" | "failed" | "stopped" | "unused";

/** The state of the Dev Tunnel; `unused` behind Tailscale. */
export type MobileDevTunnelState = "pending" | "not-installed" | "not-signed-in" | "signing-in" | "serving" | "failed" | "stopped" | "unused";

/** The status of the Mobile plugin. */
export type MobileStatus = Readonly<{
  mode: MobileMode;
  server: MobileServerState;
  /** The local port, on 127.0.0.1. */
  port: number;
  /** Why the server failed, for people. */
  serverDetail: string | null;
  tailscale: Readonly<{
    state: MobileTailscaleState;
    /** `https://<node>.ts.net:<port>` while serving. */
    url: string | null;
    /** A line for people. */
    detail: string;
    /** The page that fixes it (the download of Tailscale, the page of the admin console that enables Serve and HTTPS). */
    fixUrl: string | null;
  }>;
  devTunnel: Readonly<{
    state: MobileDevTunnelState;
    /** `https://<tunnel>-<port>.<region>.devtunnels.ms` while serving. */
    url: string | null;
    /** A line for people. */
    detail: string;
    /** The page that fixes it: the install of the command line, or the page where the code of the sign-in is entered. */
    fixUrl: string | null;
    /** While signing in, the code to enter on `fixUrl`. */
    code: string | null;
  }>;
  /** What the QR code holds: `<url>/#k=<token>`; null while nothing can be reached. */
  connectUrl: string | null;
  /** The same without the token, shown under the QR code. */
  address: string | null;
  /** The QR code of `connectUrl`: one string of '0' and '1' per row, square. */
  qr: readonly string[] | null;
}>;

const serverStates: readonly MobileServerState[] = ["starting", "listening", "port-in-use", "failed", "stopped"];
const tailscaleStates: readonly MobileTailscaleState[] = ["pending", "not-installed", "not-running", "serve-disabled", "port-taken", "serving", "failed", "stopped", "unused"];
const devTunnelStates: readonly MobileDevTunnelState[] = ["pending", "not-installed", "not-signed-in", "signing-in", "serving", "failed", "stopped", "unused"];
const isRecord = (value: unknown): value is Record<string, unknown> => typeof value === "object" && value !== null && !Array.isArray(value);
const text = (value: unknown): string | null => typeof value === "string" && value.length > 0 ? value : null;

/** Reads a status as the plugin sent it; null when it is not one. A QR code that is not a square of 0 and 1 is left out. */
export function readStatus(value: unknown): MobileStatus | null {
  if (!isRecord(value) || !isRecord(value.tailscale)) return null;
  const server = serverStates.find(state => state === value.server);
  const state = tailscaleStates.find(state => state === (value.tailscale as Record<string, unknown>).state);
  if (!server || !state) return null;
  const rows = Array.isArray(value.qr) && value.qr.length > 0 && value.qr.every(row => typeof row === "string" && row.length === (value.qr as unknown[]).length && /^[01]+$/.test(row))
    ? value.qr as string[] : null;
  const tailscale = value.tailscale;
  const tunnel = isRecord(value.devTunnel) ? value.devTunnel : {};
  return {
    mode: value.mode === "devtunnel" ? "devtunnel" : "tailscale",
    devTunnel: { state: devTunnelStates.find(known => known === tunnel.state) ?? "unused", url: text(tunnel.url), detail: text(tunnel.detail) ?? "", fixUrl: text(tunnel.fixUrl), code: text(tunnel.code) },
    server, port: typeof value.port === "number" ? value.port : 0, serverDetail: text(value.serverDetail),
    tailscale: { state, url: text(tailscale.url), detail: text(tailscale.detail) ?? "", fixUrl: text(tailscale.fixUrl) },
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
  | "Dev Tunnels is opening the tunnel." | "Waiting for the sign-in in the browser…"
  | "The Dev Tunnels command line is not installed." | "Sign in to Dev Tunnels." | "The Dev Tunnel did not open." | "The Dev Tunnel is closed."
  | "Install Dev Tunnels" | "Enter the code {code} on the sign-in page." | "Open the sign-in page";

/** Something that keeps a phone from connecting: what it is, why (the plugin's own words), and what fixes it. */
export type MobileProblem = Readonly<{
  key: "server" | "tailscale" | "devtunnel";
  /** Danger when nothing can work until it is fixed; warning otherwise; primary for a step the person takes (the code of a sign-in). */
  intent: "danger" | "warning" | "primary";
  title: MobileText;
  parameters?: Readonly<Record<string, string | number>>;
  /** The plugin's line, when it says more than the title. */
  detail: string | null;
  /** What the tab says when the plugin gives no line. */
  hint?: MobileText;
  /** A button that opens the page that fixes it. */
  fix: Readonly<{ label: MobileText; url: string }> | null;
  /** Whether "Try again" asks Tailscale again: the states that can change by themselves once fixed. */
  retry: boolean;
  /** Whether the buttons that sign in to Dev Tunnels (Microsoft, GitHub) are offered. */
  signIn?: boolean;
  /** A text the tab offers to copy: the code of the sign-in. */
  copy?: string;
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

const retryStates: readonly MobileTailscaleState[] = ["not-running", "serve-disabled", "port-taken", "failed"];

const fixLabels: Readonly<Partial<Record<MobileTailscaleState, MobileText>>> = {
  "not-installed": "Download Tailscale", "serve-disabled": "Enable it in the admin console", "not-running": "Open Tailscale",
};

const tailscaleTitles: Readonly<Partial<Record<MobileTailscaleState, MobileText>>> = {
  "not-installed": "Tailscale is not installed on this computer.",
  "not-running": "Tailscale is not running on this computer.",
  "serve-disabled": "Tailscale Serve is not enabled for this tailnet.",
  "port-taken": "The port of Tailscale Serve is used by another service.",
  "failed": "Tailscale could not publish the page.",
  "stopped": "The page is not published on the tailnet.",
};

const devTunnelTitles: Readonly<Partial<Record<MobileDevTunnelState, MobileText>>> = {
  "not-installed": "The Dev Tunnels command line is not installed.",
  "not-signed-in": "Sign in to Dev Tunnels.",
  "failed": "The Dev Tunnel did not open.",
  "stopped": "The Dev Tunnel is closed.",
};

/** What the tab shows for a status. */
export function mobileView(status: MobileStatus): MobileView {
  const problems: MobileProblem[] = [];
  const server = status.server;
  if (server === "port-in-use") {
    problems.push({ key: "server", intent: "danger", title: "The port {port} is in use by another program.", parameters: { port: status.port },
      detail: status.serverDetail, hint: "Another CodeAlta with the Mobile plugin on may be using it.", fix: null, retry: false });
  } else if (server === "failed" || server === "stopped") {
    problems.push({ key: "server", intent: "danger", title: server === "failed" ? "The server of the phone page did not start." : "The server of the phone page is stopped.",
      detail: status.serverDetail, fix: null, retry: false });
  }
  const tunnel = status.mode === "devtunnel";
  const devTunnel = status.devTunnel;
  // While signing in, the page and the code the command line gave; its own words when the code could not be read.
  if (tunnel && devTunnel.state === "signing-in" && (devTunnel.code || devTunnel.fixUrl)) {
    problems.push({
      key: "devtunnel", intent: "primary",
      title: devTunnel.code ? "Enter the code {code} on the sign-in page." : "Sign in to Dev Tunnels.", parameters: devTunnel.code ? { code: devTunnel.code } : undefined,
      detail: devTunnel.code ? null : devTunnel.detail.trim() || null,
      fix: devTunnel.fixUrl ? { label: "Open the sign-in page", url: devTunnel.fixUrl } : null, retry: false, copy: devTunnel.code ?? undefined,
    });
  }
  const tunnelTitle = tunnel ? devTunnelTitles[devTunnel.state] : undefined;
  if (tunnelTitle) {
    problems.push({
      key: "devtunnel", intent: devTunnel.state === "not-installed" ? "danger" : "warning", title: tunnelTitle,
      // The title says it all for the sign-in: the buttons follow it.
      detail: devTunnel.state === "not-signed-in" ? null : devTunnel.detail.trim() || null,
      fix: devTunnel.state === "not-installed" && devTunnel.fixUrl ? { label: "Install Dev Tunnels", url: devTunnel.fixUrl } : null,
      retry: devTunnel.state !== "not-signed-in", signIn: devTunnel.state === "not-signed-in",
    });
  }
  const tailscale = status.tailscale;
  const title = tunnel ? undefined : tailscaleTitles[tailscale.state];
  if (title) {
    problems.push({
      key: "tailscale", intent: tailscale.state === "not-installed" || tailscale.state === "serve-disabled" ? "danger" : "warning", title,
      detail: tailscale.detail.trim() || null,
      fix: tailscale.fixUrl ? { label: fixLabels[tailscale.state] ?? "Open the page that fixes it", url: tailscale.fixUrl } : null,
      retry: retryStates.includes(tailscale.state),
    });
  }
  const code = status.connectUrl && status.qr ? { ...qrPath(status.qr), address: status.address ?? status.connectUrl.replace(/#.*$/u, "") } : null;
  const waiting: MobileText | null = code || problems.some(problem => problem.key === "server" || problem.key === "devtunnel") ? null
    : tunnel && devTunnel.state === "signing-in" ? "Waiting for the sign-in in the browser…"
    : server === "starting" ? "The phone page is starting."
    : tunnel && devTunnel.state === "pending" ? "Dev Tunnels is opening the tunnel."
    : !tunnel && tailscale.state === "pending" ? "Tailscale is starting to publish the page." : null;
  const instruction: MobileText = tunnel
    ? "Scan it with the camera of the phone. The first time, the phone shows a page of Microsoft: choose Continue."
    : "Scan it with the camera of the phone. The phone must be signed in to the same tailnet.";
  return { code, problems, waiting, instruction };
}
