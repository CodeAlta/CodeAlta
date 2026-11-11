import type { CredentialProviderRpcView, CredentialTypeView, CredentialSettingInput, CredentialSettingValue, SecretView } from "#neoastra";
import type { MessageKey } from "../localization";

/** The state of a provider or of a secret, as the host names it. */
export type CredentialState = "ready" | "locked" | "sign_in" | "not_installed" | "waits" | "not_found" | "unavailable";

/** What a state is called, and how it is drawn: a state that needs the user is a warning, one that cannot be fixed here a danger. */
export function stateLabel(state: string): { label: MessageKey; intent: "success" | "warning" | "danger" | "none" } {
  switch (state) {
    case "ready": return { label: "Ready", intent: "success" };
    case "locked": return { label: "Locked", intent: "warning" };
    case "sign_in": return { label: "Sign-in needed", intent: "warning" };
    case "not_installed": return { label: "Not installed", intent: "warning" };
    case "waits": return { label: "Waits for you", intent: "warning" };
    case "not_found": return { label: "Not found", intent: "danger" };
    default: return { label: "Unavailable", intent: "danger" };
  }
}

/** How long the values of a provider stay in memory, as the page offers them. */
export const cacheDurations: readonly { value: string; label: MessageKey }[] = [
  { value: "session", label: "Until CodeAlta closes" },
  { value: "8h", label: "8 hours" },
  { value: "1h", label: "1 hour" },
  { value: "15m", label: "15 minutes" },
];

/** How long a confirmation of a secret that asks before use lasts. */
export const askDurations: readonly { value: string; label: MessageKey }[] = [
  { value: "15m", label: "15 minutes" },
  { value: "1h", label: "1 hour" },
  { value: "session", label: "Until CodeAlta closes" },
  { value: "each", label: "Each use" },
];

/** The id a new provider of a type is given: the type, or the first free one after it (`-2`, `-3`...). */
export function suggestedProviderId(type: string, taken: readonly string[]): string {
  // A type that starts with a digit is no id: 1Password is the vault of a person first.
  const base = type === "1password" ? "personal" : type.toLowerCase().replace(/[^a-z0-9_-]+/g, "-").replace(/^[^a-z]+/, "").replace(/-+$/g, "") || "vault";
  if (!taken.includes(base)) return base;
  for (let index = 2; ; index++) if (!taken.includes(`${base}-${index}`)) return `${base}-${index}`;
}

/** Whether a text can be the id of a provider: lowercase letters, digits, - and _, starting with a letter. */
export function isProviderId(id: string): boolean {
  return /^[a-z][a-z0-9_-]{0,63}$/.test(id);
}

/** The settings of a provider as its form edits them: a text per key, lists one item a line. */
export type SettingDrafts = Record<string, string | boolean>;

/** The drafts of the form of a provider, from what the configuration holds. */
export function draftsOf(type: CredentialTypeView, values: readonly CredentialSettingValue[] | null): SettingDrafts {
  const drafts: SettingDrafts = {};
  for (const setting of type.settings) {
    const value = values?.find(item => item.key === setting.key);
    drafts[setting.key] = setting.kind === "boolean" ? value?.flag ?? false
      : setting.kind === "list" ? (value?.list ?? []).join("\n")
      : value?.text ?? "";
  }
  return drafts;
}

/** The settings the page sends for the drafts of a form. */
export function settingsOf(type: CredentialTypeView, drafts: SettingDrafts): CredentialSettingInput[] {
  return type.settings.map(setting => {
    const draft = drafts[setting.key];
    if (setting.kind === "boolean") return { key: setting.key, text: null, flag: draft === true, list: null };
    const text = typeof draft === "string" ? draft : "";
    if (setting.kind === "list") return { key: setting.key, text: null, flag: null, list: text.split(/\r?\n/).map(line => line.trim()).filter(line => line.length > 0) };
    return { key: setting.key, text: text.trim() || null, flag: null, list: null };
  });
}

/** What is missing from the drafts of a form, the label of the first required setting left empty; null when nothing is. */
export function missingSetting(type: CredentialTypeView, drafts: SettingDrafts): string | null {
  for (const setting of type.settings) {
    if (!setting.required) continue;
    const draft = drafts[setting.key];
    if (typeof draft !== "string" || draft.trim().length === 0) return setting.label;
  }
  return null;
}

/** Whether a setting of kind secret holds a reference to a secret of this computer, as the form needs it. */
export function isSecretReference(text: string): boolean {
  return /^\$\{secret:[A-Za-z_][A-Za-z0-9_]{0,127}\}$/.test(text.trim());
}

/** The label a picked field is kept under: the path of the vault and the name of the field. */
export function pickedLabel(path: readonly string[], field: string): string {
  return [...path, field].join(" › ");
}

/** Whether a name holds every word of a search, whatever their case and accents: `git hub` finds `GitHub`, `ecole` finds `École`. */
export function matchesQuery(query: string, name: string): boolean {
  const fold = (text: string) => text.normalize("NFD").replace(/\p{M}/gu, "").toLowerCase();
  const words = fold(query).split(/\s+/).filter(word => word.length > 0);
  if (words.length === 0) return true;
  const folded = fold(name);
  const joined = folded.replace(/[^\p{L}\p{N}]+/gu, "");
  return words.every(word => folded.includes(word) || joined.includes(word.replace(/[^\p{L}\p{N}]+/gu, "")));
}

/**
 * The name a secret is given from the field picked in a vault (`Private › GitHub › password`): the item and what the field is,
 * as an environment variable is named, such as `GITHUB_PASSWORD` or `ANTHROPIC_API_KEY` for the credential of an API key.
 */
export function suggestedSecretName(label: string): string {
  const parts = label.split(" › ").map(part => part.trim()).filter(part => part.length > 0);
  const field = parts.at(-1) ?? "";
  const item = parts.length > 1 ? parts.at(-2)! : "";
  const words = (text: string) => text.normalize("NFD").replace(/\p{M}/gu, "").toUpperCase().replace(/[^A-Z0-9]+/g, "_").replace(/^_+|_+$/g, "");
  const kind = /credential|api.?key/i.test(field) ? "API_KEY"
    : /password|passwort|mot de passe/i.test(field) ? "PASSWORD"
    : /one.?time|otp|totp/i.test(field) ? "TOTP"
    : /token/i.test(field) ? "TOKEN"
    : /user.?name|login|e-?mail/i.test(field) ? "USERNAME"
    : words(field);
  const name = [words(item), kind].filter(part => part.length > 0).join("_").replace(/_+/g, "_") || "SECRET";
  return /^[0-9]/.test(name) ? `SECRET_${name}` : name;
}

/** The secrets of the listing kept in a vault of a provider. */
export function secretsOf(provider: string, secrets: readonly SecretView[]): SecretView[] {
  return secrets.filter(secret => secret.provider === provider);
}

/** The provider of a secret, or null for a secret of this computer. */
export function providerOf(secret: SecretView, providers: readonly CredentialProviderRpcView[]): CredentialProviderRpcView | null {
  return secret.provider ? providers.find(provider => provider.id === secret.provider) ?? null : null;
}

/** What a listing without vaults holds of them: a machine of the fleet, a host without a broker. */
export const noCredentials = { providers: [], types: [], pending: [], uses: [], verification: null, canPrompt: false, developerInstance: false, developerUnlock: false, configurationProblems: [], signIns: [] } as const;

/** A secret of this computer as a listing gives it, from what every secret has. */
export function localSecret(secret: Pick<SecretView, "name" | "version" | "setBy" | "setAt">): SecretView {
  return { ...secret, provider: null, providerName: null, icon: null, label: null, askBeforeUse: null, state: "ready", stateMessage: null };
}

/** A sign-in as its dialog edits it: references of the vault, never a value. */
export type SignInDraft = Readonly<{ id: string | null; label: string; provider: string; sites: readonly string[]; username: string; password: string; totp: string }>;

/** The sites typed in the dialog of a sign-in, one a line or separated by commas, without the empty ones. */
export function siteList(text: string): string[] {
  return text.split(/[\r\n,]+/).map(line => line.trim()).filter(line => line.length > 0);
}

/** Whether a text is a site as a sign-in lists it: an origin, https (http only for this computer), without a path. */
export function isSite(text: string): boolean {
  const value = text.includes("://") ? text : `https://${text}`;
  try {
    const url = new URL(value);
    const loopback = url.hostname === "localhost" || url.hostname === "127.0.0.1" || url.hostname === "[::1]";
    return (url.protocol === "https:" || url.protocol === "http:" && loopback) && !url.username && !url.password
      && (url.pathname === "/" || url.pathname === "") && !url.search && !url.hash && !/\/[^/]/.test(value.replace(/^[a-z]+:\/\//i, ""));
  } catch {
    return false;
  }
}

/** What keeps a sign-in from being saved; null when it can be. */
export function signInDraftProblem(draft: SignInDraft): MessageKey | null {
  if (!draft.label.trim()) return "A sign-in needs a name.";
  if (draft.sites.length === 0 || !draft.sites.every(isSite)) return "A site is an address such as https://github.com, without a path.";
  if (!draft.provider) return "Add a vault first: a sign-in is an item of a vault.";
  if (!draft.username.trim() || !draft.password.trim()) return "A sign-in needs the user name and the password of an item.";
  return null;
}

/** A command line as the terminal takes it: each word quoted when it holds a space. */
export function commandLine(words: readonly string[]): string {
  return words.map(word => /[\s"]/.test(word) ? `"${word.replaceAll("\"", "\\\"")}"` : word).join(" ");
}
