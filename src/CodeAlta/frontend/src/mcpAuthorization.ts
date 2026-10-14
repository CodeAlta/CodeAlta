import type { MessageKey } from "./localization";

/** Keeps only an http or https address from a `prompt` event of an MCP server authorization. */
export function authorizationAddress(event: { url: string | null }): string | null {
  return typeof event.url === "string" && /^https?:\/\/\S+$/u.test(event.url) && event.url.length <= 8192 ? event.url : null;
}

/** When the host stops waiting for the browser, from a `prompt` event, as milliseconds since the epoch, or null. */
export function authorizationDeadline(event: { expiresAt?: string | null }): number | null {
  const deadline = typeof event.expiresAt === "string" ? Date.parse(event.expiresAt) : Number.NaN;
  return Number.isFinite(deadline) ? deadline : null;
}

/** The time the browser has left to come back, as `m:ss`, never below `0:00`. */
export function authorizationTimeLeft(deadline: number, now: number): string {
  const seconds = Math.max(0, Math.ceil((deadline - now) / 1000));
  return `${Math.floor(seconds / 60)}:${String(seconds % 60).padStart(2, "0")}`;
}

/** Why an authorization is not offered for a server right now, or null. */
export function authorizationBlocked(server: { enabled: boolean; shadowed: boolean }, dirty: boolean): MessageKey | null {
  return dirty ? "Save before authorizing."
    : server.shadowed ? "Another definition of this server is the one in use; authorize that one."
    : !server.enabled ? "Enable the server before authorizing." : null;
}

/** The notice for a failed authorization, by its code; the host's explanation is shown when it gave one. */
export function authorizationFailure(code: string | null, detail: string | null): { key: MessageKey; intent: "warning" | "danger"; detail?: string } {
  switch (code) {
    case "canceled": return { key: "Authorization canceled.", intent: "warning" };
    case "timeout": return { key: "The authorization timed out. Start it again.", intent: "warning" };
    case "busy": return { key: "Another authorization is already running.", intent: "warning" };
    case "shadowed": return { key: "Another definition of this server is the one in use; authorize that one.", intent: "warning" };
    case "stale_epoch": case "unavailable": return { key: "The app restarted; reopen Settings and try again.", intent: "warning" };
    default: return detail ? { key: "The authorization did not complete: {detail}", intent: "danger", detail }
      : { key: "The authorization did not complete.", intent: "danger" };
  }
}
