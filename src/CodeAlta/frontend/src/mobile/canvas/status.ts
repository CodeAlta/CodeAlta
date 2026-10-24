// What the Mobile tab shows, from the status the Mobile plugin gives (`mobile.status`, `mobile.retry`, the event `mobile.changed`):
// the QR code of the link, and what keeps a phone from connecting, with what fixes it. Plain functions, without the DOM.

/** The state of the plugin's server on 127.0.0.1. */
export type MobileServerState = "starting" | "listening" | "port-in-use" | "failed" | "stopped";

/** The state of the page's publication on the tailnet, with `tailscale serve`. */
export type MobileTailscaleState = "pending" | "not-installed" | "not-running" | "serve-disabled" | "port-taken" | "serving" | "failed" | "stopped";

/** The status of the Mobile plugin. */
export type MobileStatus = Readonly<{
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
  /** What the QR code holds: `<url>/#k=<token>`; null while nothing can be reached. */
  connectUrl: string | null;
  /** The same without the token, shown under the QR code. */
  address: string | null;
  /** The QR code of `connectUrl`: one string of '0' and '1' per row, square. */
  qr: readonly string[] | null;
}>;

const serverStates: readonly MobileServerState[] = ["starting", "listening", "port-in-use", "failed", "stopped"];
const tailscaleStates: readonly MobileTailscaleState[] = ["pending", "not-installed", "not-running", "serve-disabled", "port-taken", "serving", "failed", "stopped"];
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
  return {
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
  | "Download Tailscale" | "Enable it in the admin console" | "Open Tailscale" | "Open the page that fixes it";

/** Something that keeps a phone from connecting: what it is, why (the plugin's own words), and what fixes it. */
export type MobileProblem = Readonly<{
  key: "server" | "tailscale";
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
  /** Whether "Try again" asks Tailscale again: the states that can change by themselves once fixed. */
  retry: boolean;
}>;

/** What the tab shows: the code when there is one, what keeps a phone from connecting, and what is still starting. */
export type MobileView = Readonly<{
  code: Readonly<{ size: number; path: string; address: string }> | null;
  problems: readonly MobileProblem[];
  /** A step that is not done yet and is nobody's fault, while there is no code. */
  waiting: MobileText | null;
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
  const tailscale = status.tailscale;
  const title = tailscaleTitles[tailscale.state];
  if (title) {
    problems.push({
      key: "tailscale", intent: tailscale.state === "not-installed" || tailscale.state === "serve-disabled" ? "danger" : "warning", title,
      detail: tailscale.detail.trim() || null,
      fix: tailscale.fixUrl ? { label: fixLabels[tailscale.state] ?? "Open the page that fixes it", url: tailscale.fixUrl } : null,
      retry: retryStates.includes(tailscale.state),
    });
  }
  const code = status.connectUrl && status.qr ? { ...qrPath(status.qr), address: status.address ?? status.connectUrl.replace(/#.*$/u, "") } : null;
  const waiting: MobileText | null = code || problems.some(problem => problem.key === "server") ? null
    : server === "starting" ? "The phone page is starting."
    : tailscale.state === "pending" ? "Tailscale is starting to publish the page." : null;
  return { code, problems, waiting };
}
