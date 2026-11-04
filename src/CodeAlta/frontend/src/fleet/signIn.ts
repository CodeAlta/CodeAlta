import type { FleetSignInCodeView, FleetSignInMethodView, FleetSignInSettingsView } from "#neoastra";
import type { MessageKey } from "../localization";

/** The sign-in methods a server may offer, in the order the page shows them. */
export const signInMethodOrder = ["company", "passkey", "microsoft", "google", "github"] as const;

/** The sign-in methods a person links from one of their computers. */
export const linkableMethods = ["passkey", "microsoft", "google", "github"] as const;

/** The methods a server offers, ordered: the company's first. */
export function orderedSignInMethods(methods: readonly FleetSignInMethodView[]): FleetSignInMethodView[] {
  const rank = (id: string) => { const at = signInMethodOrder.indexOf(id as typeof signInMethodOrder[number]); return at < 0 ? signInMethodOrder.length : at; };
  return [...methods].sort((left, right) => rank(left.id) - rank(right.id));
}

/** What a failed sign-in says, for the statuses of sign-in; null for the others. */
export function signInProblem(status: string): MessageKey | null {
  switch (status) {
    case "sign_in.cancelled": return "The sign-in was cancelled.";
    case "sign_in.denied": return "The sign-in was refused at the provider.";
    case "sign_in.expired": return "The code expired before it was entered: start the sign-in again.";
    case "sign_in.browser": return "The browser of this computer did not open the sign-in.";
    case "sign_in.timeout": case "sign_in.invalid_attempt": return "The sign-in took too long: start it again.";
    case "sign_in.method_disabled": return "This server does not offer this sign-in.";
    case "sign_in.sso_required": return "Your organization requires its company sign-in.";
    case "sign_in.not_linked": return "No one at this server linked this account: sign in on one of your computers and link it first.";
    case "sign_in.linked_elsewhere": return "This account is linked to another person of the organization.";
    case "sign_in.provider_unavailable": return "The sign-in provider does not answer.";
    case "sign_in.invalid_settings": return "These sign-in settings do not hold.";
    case "sign_in.code_refused": case "sign_in.invalid_token": case "sign_in.wrong_audience": case "sign_in.wrong_issuer": case "sign_in.wrong_nonce":
    case "sign_in.token_expired": case "sign_in.invalid_passkey": case "sign_in.state_mismatch": case "sign_in.invalid_grant":
      return "The sign-in did not verify: start it again.";
    default: return null;
  }
}

/** The code of a GitHub sign-in, as the page shows it, with the handle the host continues it with. */
export type SignInCode = Readonly<{ handle: string; userCode: string; url: string }>;

/** The code the host gave, when it is one to show: a short printable code and an https:// page (or one on the loopback, in tests); null otherwise. */
export function signInCode(view: FleetSignInCodeView | null | undefined): SignInCode | null {
  if (!view || typeof view.userCode !== "string" || !/^[\x21-\x7e]{1,64}$/u.test(view.userCode) || typeof view.handle !== "string" || !/^[0-9a-f]{8,64}$/u.test(view.handle)) return null;
  let url: URL;
  try { url = new URL(view.url); } catch { return null; }
  const loopback = url.protocol === "http:" && ["127.0.0.1", "localhost", "[::1]"].includes(url.hostname);
  return url.protocol === "https:" || loopback ? { handle: view.handle, userCode: view.userCode, url: url.href } : null;
}

/** The settings of sign-in as the form edits them. */
export interface SignInDraft {
  enabled: string[];
  issuer: string;
  clientId: string;
  /** The secret typed; empty keeps the one set. */
  clientSecret: string;
  displayName: string;
  groupsClaim: string;
  /** The groups that give a role at each company sign-in. */
  groupRoles: { group: string; role: string }[];
  requireSso: boolean;
}

/** The roles a group may give: every role but owner, from the widest. */
export const groupRoleChoices = ["admin", "member", "auditor", "guest"] as const;

/** The draft of settings as the server holds them. */
export function signInDraft(view: FleetSignInSettingsView): SignInDraft {
  return {
    enabled: view.methods.filter(method => method.enabled).map(method => method.id), issuer: view.issuer ?? "", clientId: view.clientId ?? "", clientSecret: "",
    displayName: view.displayName ?? "", groupsClaim: view.groupsClaim || "groups", groupRoles: (view.groupRoles ?? []).map(role => ({ ...role })), requireSso: view.requireSso,
  };
}

/** Whether the draft names a company provider. */
export function hasCompany(draft: SignInDraft): boolean {
  return draft.issuer.trim().length > 0 || draft.clientId.trim().length > 0;
}

/** Why a draft cannot be saved; null when it can. */
export function signInDraftProblem(draft: SignInDraft): MessageKey | null {
  if (hasCompany(draft)) {
    let url: URL | null = null;
    try { url = new URL(draft.issuer.trim()); } catch { url = null; }
    if (!url || url.protocol !== "https:" && !(url.protocol === "http:" && ["127.0.0.1", "localhost", "[::1]"].includes(url.hostname)) || url.search || url.hash)
      return "The issuer is an https:// address.";
    if (!draft.clientId.trim()) return "Company sign-in needs its client id.";
  } else if (draft.enabled.includes("company")) {
    return "Company sign-in needs its issuer and client id.";
  }
  if (draft.requireSso && (!hasCompany(draft) || !draft.enabled.includes("company"))) return "Requiring company sign-in needs company sign-in on.";
  const groups = draft.groupRoles.map(role => role.group.trim());
  if (groups.some(group => !group)) return "Each group needs its id.";
  if (new Set(groups).size !== groups.length) return "A group gives one role.";
  return null;
}

/** What the row of sign-in says: the methods on, and whether company sign-in is required. */
export function signInSummary(view: FleetSignInSettingsView): { key: MessageKey; methods: string } {
  const names = view.methods.filter(method => method.enabled && method.available).map(method => method.name);
  return { key: view.requireSso ? "Company sign-in required" : names.length === 0 ? "No sign-in: enrollment codes only" : "Sign in with {methods}", methods: names.join(", ") };
}
