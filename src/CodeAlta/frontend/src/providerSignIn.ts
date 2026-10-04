import type { MessageKey } from "./localization";

/** What a running sign-in asks the user to do: open an address and, for a device flow, enter a code there. */
export type LoginPrompt = Readonly<{ url: string | null; userCode: string | null; browserOpened: boolean }>;

/** Keeps only an https address and a short printable code from a `prompt` event. */
export function loginPrompt(event: { url: string | null; userCode: string | null; browserOpened: boolean }): LoginPrompt {
  const url = typeof event.url === "string" && /^https:\/\/\S+$/u.test(event.url) && event.url.length <= 8192 ? event.url : null;
  const userCode = typeof event.userCode === "string" && /^[\x21-\x7e]{1,64}$/u.test(event.userCode) ? event.userCode : null;
  return { url, userCode, browserOpened: event.browserOpened === true && url !== null };
}

/** The button label of a sign-in method. */
export function loginModeLabel(mode: string): MessageKey {
  return mode === "device" ? "Sign in with a device code" : "Sign in with the browser";
}

/** The notice for a failed sign-in, by its code. */
export function loginFailure(code: string | null): { key: MessageKey; intent: "warning" | "danger" } {
  switch (code) {
    case "canceled": return { key: "Sign-in canceled.", intent: "warning" };
    case "timeout": return { key: "The sign-in timed out. Start it again.", intent: "warning" };
    case "busy": return { key: "Another sign-in is already running.", intent: "warning" };
    case "stale_epoch": case "unavailable": return { key: "The app restarted; reopen Settings and try again.", intent: "warning" };
    default: return { key: "The sign-in did not complete.", intent: "danger" };
  }
}

/** A provider default shown for a blank field: the built-in value for this provider, or for its adapter type. */
export function providerDefault<Field extends string>(field: Field, type: string, original: { type: string; defaults: Readonly<Record<Field, string | null>> } | null,
  typeDefaults: readonly { type: string; defaults: Readonly<Record<Field, string | null>> }[]): string | null {
  const own = original && original.type === type ? original.defaults[field] : null;
  return own ?? typeDefaults.find(entry => entry.type === type)?.defaults[field] ?? null;
}
