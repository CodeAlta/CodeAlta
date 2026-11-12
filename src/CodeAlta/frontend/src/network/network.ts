/**
 * The proxy of CodeAlta as its settings page edits it: the form, what makes it valid, the requests it sends, and what a
 * check of the connection found. No DOM here.
 */
import type { NetworkCheckRequest, NetworkCheckResponse, NetworkProxyResponse, NetworkProxySaveRequest } from "#neoastra";
import type { MessageKey } from "../localization";

/** How CodeAlta reaches the network: as the system says, directly, or through the proxy of the form. */
export type ProxyMode = "system" | "none" | "manual";
/** How CodeAlta signs in to a manual proxy. */
export type ProxyAuthentication = "none" | "account" | "password";

/** The form of the page. `password` is a new password; empty keeps the one kept. */
export type ProxyForm = Readonly<{ mode: ProxyMode; url: string; bypass: string; authentication: ProxyAuthentication; user: string; password: string }>;

/** The address a check requests when none is typed. */
export const defaultCheckTarget = "https://www.codealta.io";

/** The secret the page keeps the password in, as the host names it. */
export const passwordSecret = "NETWORK_PROXY_PASSWORD";

const modes: readonly ProxyMode[] = ["system", "none", "manual"];
const authentications: readonly ProxyAuthentication[] = ["none", "account", "password"];

/** The mode of a text of the host; an unknown one is the system mode. */
export const modeOf = (value: string | null | undefined): ProxyMode => modes.includes(value as ProxyMode) ? value as ProxyMode : "system";

/** The authentication of a text of the host; an unknown one is none. */
export const authenticationOf = (value: string | null | undefined): ProxyAuthentication =>
  authentications.includes(value as ProxyAuthentication) ? value as ProxyAuthentication : "none";

/** The form of the settings the host keeps: the hosts one per line, no password. */
export function formOf(listing: NetworkProxyResponse): ProxyForm {
  return { mode: modeOf(listing.mode), url: listing.url, bypass: listing.bypass.join("\n"), authentication: authenticationOf(listing.authentication), user: listing.user, password: "" };
}

/** The entries of a bypass list as it is typed: separated by commas, semicolons, spaces or lines, each once. */
export function bypassEntries(text: string): string[] {
  const seen = new Set<string>();
  const entries: string[] = [];
  for (const entry of text.split(/[\s,;]+/)) {
    const value = entry.trim();
    if (value && !seen.has(value.toLowerCase())) { seen.add(value.toLowerCase()); entries.push(value); }
  }
  return entries;
}

/** Whether a text is the address of a proxy the host takes: http or https, a host, an optional port, nothing else. */
export function isProxyAddress(text: string): boolean {
  const value = text.trim();
  if (!value) return false;
  let url: URL;
  try { url = new URL(value.includes("://") ? value : `http://${value}`); } catch { return false; }
  return (url.protocol === "http:" || url.protocol === "https:") && !!url.hostname && !url.username && !url.password
    && (url.pathname === "/" || url.pathname === "") && !url.search && !url.hash;
}

/** Why the form cannot be saved or checked, or null. The host checks it again. */
export function formError(form: ProxyForm, passwordSet: boolean): MessageKey | null {
  if (form.mode !== "manual") return null;
  if (!form.url.trim()) return "Type the address of the proxy, such as http://proxy.example.com:8080.";
  if (/^[a-z][a-z0-9+.-]*:\/\/[^/]*@/i.test(form.url.trim())) return "Put the user name and password in the authentication of the proxy, not in its address.";
  if (!isProxyAddress(form.url)) return "The address of the proxy must be http://host:port, without a path.";
  if (form.authentication === "password" && !form.user.trim()) return "Type the user name of the proxy.";
  if (form.authentication === "password" && !form.password && !passwordSet) return "Type the password of the proxy.";
  return null;
}

/** Whether the form differs from the settings the host keeps. A password typed is a change. */
export function formChanged(form: ProxyForm, listing: NetworkProxyResponse): boolean {
  const saved = formOf(listing);
  return form.mode !== saved.mode || form.url.trim() !== saved.url || bypassEntries(form.bypass).join("\n") !== listing.bypass.join("\n")
    || form.authentication !== saved.authentication || form.user.trim() !== saved.user || form.password !== "";
}

/** The request that saves the form. */
export function saveRequest(form: ProxyForm, epoch: string | null): NetworkProxySaveRequest {
  return { expectedEpoch: epoch, mode: form.mode, url: form.url.trim() || null, bypass: bypassEntries(form.bypass), authentication: form.authentication,
    user: form.user.trim() || null, password: form.password || null };
}

/** The request that checks the connection through the form as it is, saved or not. */
export function checkRequest(form: ProxyForm, target: string, epoch: string | null): NetworkCheckRequest {
  const { expectedEpoch, mode, url, bypass, authentication, user, password } = saveRequest(form, epoch);
  return { expectedEpoch, target: target.trim() || defaultCheckTarget, mode, url, bypass, authentication, user, password };
}

/** What the page says of a check: its words, their values, and whether it went well. */
export type CheckSummary = Readonly<{ key: MessageKey; values: Record<string, string | number>; intent: "success" | "warning" | "danger"; detail: string | null }>;

/** Describes what a check found: the answer and the way it came, or why the address was not reached. */
export function describeCheck(result: NetworkCheckResponse): CheckSummary {
  const target = result.target ?? defaultCheckTarget;
  if (result.status !== "ok") return { key: "The connection could not be checked.", values: {}, intent: "danger", detail: result.message };
  if (!result.reached) {
    return result.httpStatus === 407
      ? { key: "The proxy asks for a user name and password, or refused them.", values: {}, intent: "danger", detail: result.message }
      : { key: "{target} could not be reached.", values: { target }, intent: "danger", detail: result.message };
  }
  const values = { target, status: result.httpStatus ?? 0, milliseconds: result.elapsedMilliseconds, proxy: result.via ?? "" };
  return result.via
    ? { key: "{target} answered {status} through {proxy} in {milliseconds} ms.", values, intent: "success", detail: null }
    : { key: "{target} answered {status} directly in {milliseconds} ms.", values, intent: "success", detail: null };
}
