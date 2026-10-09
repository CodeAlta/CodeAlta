import type { GlobalConfigProvider, GlobalConfigProviderEdit } from "#neoastra";
import { brandColor } from "./brands";
import type { MessageKey } from "./localization";

/** Editable provider fields; empty strings mean "left to the provider's default". */
export type ProviderForm = Readonly<{
  key: string; type: string; enabled: boolean; displayName: string; model: string; reasoningEffort: string;
  apiUrl: string; apiKeyEnv: string; apiKey: string; clearApiKey: boolean; makeDefault: boolean;
  /** The id of the icon chosen for the provider, and the color it is drawn in; blank when they follow the provider. */
  icon: string; color: string;
  /** For a provider that runs Claude Code: `use` or `ignore` for ANTHROPIC_API_KEY; blank follows the answer Claude Code saved. */
  anthropicApiKey: string;
  /** The permission mode a provider that runs its own CLI starts its sessions in; blank for the CLI's own setting. */
  permissionMode: string;
}>;

/** The form for an existing provider, or a blank form for a new one. */
export function providerForm(provider: GlobalConfigProvider | null, defaultProvider: string | null, types: readonly string[]): ProviderForm {
  return provider
    ? { key: provider.key, type: provider.type, enabled: provider.enabled, displayName: provider.displayName ?? "", model: provider.model ?? "",
      reasoningEffort: provider.reasoningEffort ?? "", apiUrl: provider.apiUrl ?? "", apiKeyEnv: provider.apiKeyEnv ?? "", apiKey: "",
      clearApiKey: false, makeDefault: provider.key === defaultProvider, icon: provider.icon ?? "", color: provider.color ?? "",
      anthropicApiKey: provider.anthropicApiKey ?? "", permissionMode: provider.permissionMode ?? "" }
    : { key: "", type: types[0] ?? "openai-chat", enabled: true, displayName: "", model: "", reasoningEffort: "", apiUrl: "", apiKeyEnv: "",
      apiKey: "", clearApiKey: false, makeDefault: false, icon: "", color: "", anthropicApiKey: "", permissionMode: "" };
}

export function providerFormDirty(form: ProviderForm, baseline: ProviderForm): boolean {
  return (Object.keys(form) as (keyof ProviderForm)[]).some(field => form[field] !== baseline[field]);
}

/** The first problem that prevents saving, or null. `originalKey` is null for a new provider. */
export function validateProviderForm(form: ProviderForm, providers: readonly Pick<GlobalConfigProvider, "key">[], originalKey: string | null): MessageKey | null {
  const key = form.key.trim().toLowerCase();
  if (!key) return "Enter a provider key.";
  if (key.length > 64 || !/^[a-z0-9_-]+$/.test(key)) return "A provider key uses letters, digits, '-' or '_' (at most 64).";
  if (providers.some(provider => provider.key.toLowerCase() === key && provider.key.toLowerCase() !== originalKey?.toLowerCase())) return "Another provider already uses this key.";
  if (form.apiUrl.trim() && !/^https?:\/\/\S+$/i.test(form.apiUrl.trim())) return "The API URL must start with http:// or https://.";
  if (form.makeDefault && !form.enabled) return "Only an enabled provider can be the default.";
  if (form.color.trim() && !brandColor(form.color)) return "A color is written as #rgb or #rrggbb.";
  return null;
}

/** The wire edit for a form: blank optional fields are sent as null, and an untouched secret is kept. */
export function providerEdit(form: ProviderForm): GlobalConfigProviderEdit {
  const optional = (value: string) => value.trim() ? value.trim() : null;
  // A provider that runs its own CLI takes none of the API fields: what another type left in the form is not sent.
  const cli = runsOwnCli(form.type);
  return { key: form.key.trim().toLowerCase(), type: form.type, enabled: form.enabled, displayName: optional(form.displayName), model: optional(form.model),
    reasoningEffort: optional(form.reasoningEffort), apiUrl: cli ? null : optional(form.apiUrl), apiKeyEnv: cli ? null : optional(form.apiKeyEnv),
    apiKey: cli || form.clearApiKey || !form.apiKey ? null : form.apiKey, clearApiKey: cli ? true : form.clearApiKey,
    icon: optional(form.icon)?.toLowerCase() ?? null, color: optional(form.color), anthropicApiKey: cli ? optional(form.anthropicApiKey) : null,
    // Only a provider that runs its own CLI takes a permission mode: the configuration file refuses it elsewhere.
    permissionMode: cli ? optional(form.permissionMode) : null };
}

/** Provider types that sign in through their own account flow instead of an API key. */
export function usesAccountSignIn(type: string): boolean {
  return type === "codex" || type === "copilot" || type === "xai";
}

/**
 * Provider types that run a CLI the user installed and signed in to: CodeAlta has no key, no endpoint and no
 * sign-in of its own for them.
 */
export function runsOwnCli(type: string): boolean {
  return type === "claude-code";
}

/** What to do about a provider that is not ready, when its test gave one of the reasons the host names. */
export function providerProblem(reason: string | null | undefined): MessageKey | null {
  return reason === "claude-code-signed-out" ? "Claude Code is not signed in. Run claude in a terminal and use /login, then test again."
    : reason === "claude-code-not-found" ? "Claude Code was not found. Install it, or set command of the provider to the path of its executable in the configuration file, then test again."
    : reason === "claude-code-unavailable" ? "Claude Code did not start or did not answer. Run claude in a terminal to check it, then test again."
    : null;
}
