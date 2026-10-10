import type { SessionRemoteControlResponse, SessionRuntimeStateEntry } from "#neoastra";

/** Where the Remote Control of a session stands (Claude Code's, from claude.ai and the Claude app). */
export type RemoteControlStatus = "off" | "connecting" | "connected" | "failed";

/** The Remote Control of a session as the page shows it. */
export type RemoteControlView = Readonly<{ status: RemoteControlStatus; url: string | null; error: string | null }>;

/** A session whose Remote Control is off. */
export const remoteControlOff: RemoteControlView = Object.freeze({ status: "off", url: null, error: null });

const statuses: ReadonlySet<string> = new Set(["off", "connecting", "connected", "failed"]);

/**
 * The Remote Control a host says a session has: off for what is not one. The link is kept only when it is one of
 * claude.ai, which the page opens in the browser; the error only while it failed.
 */
export function remoteControlView(value: SessionRemoteControlResponse | null | undefined): RemoteControlView {
  if (!value || typeof value !== "object" || typeof value.status !== "string" || !statuses.has(value.status)) return remoteControlOff;
  const url = typeof value.sessionUrl === "string" && value.sessionUrl.length <= 512 && value.sessionUrl.startsWith("https://claude.ai/") ? value.sessionUrl : null;
  const error = value.status === "failed" && typeof value.error === "string" && value.error.trim() ? value.error.trim().slice(0, 512) : null;
  return value.status === "off" ? remoteControlOff : Object.freeze({ status: value.status as RemoteControlStatus, url, error });
}

/** The Remote Control in the runtime state of a session: off for an attachment that ends, or a host that says none. */
export function entryRemoteControl(entry: SessionRuntimeStateEntry | null | undefined): RemoteControlView {
  return !entry || entry.isTerminated || entry.isRetiring ? remoteControlOff : remoteControlView(entry.remoteControl);
}

export function sameRemoteControl(a: RemoteControlView, b: RemoteControlView): boolean {
  return a.status === b.status && a.url === b.url && a.error === b.error;
}

/**
 * A request of the Actions menu of a session to show its Remote Control: a new one for each use of the menu. The
 * button that shows it says it is done, so that one mounted again later (another space, the tab opened again) does
 * not show it again unasked.
 */
export type RemoteControlOpenRequest = Readonly<{ done: () => void }>;

/** Whether a provider of that type has Remote Control: Claude Code. */
export function providerHasRemoteControl(type: string | null | undefined): boolean {
  return type === "claude-code";
}
