import type { GlobalConfigProvider, GlobalConfigProviderEdit } from "#neoastra";
import type { MessageKey } from "./localization";

/** Editable provider fields; empty strings mean "left to the provider's default". */
export type ProviderForm = Readonly<{
  key: string; type: string; enabled: boolean; displayName: string; model: string; reasoningEffort: string;
  apiUrl: string; apiKeyEnv: string; apiKey: string; clearApiKey: boolean; makeDefault: boolean;
}>;

/** The form for an existing provider, or a blank form for a new one. */
export function providerForm(provider: GlobalConfigProvider | null, defaultProvider: string | null, types: readonly string[]): ProviderForm {
  return provider
    ? { key: provider.key, type: provider.type, enabled: provider.enabled, displayName: provider.displayName ?? "", model: provider.model ?? "",
      reasoningEffort: provider.reasoningEffort ?? "", apiUrl: provider.apiUrl ?? "", apiKeyEnv: provider.apiKeyEnv ?? "", apiKey: "",
      clearApiKey: false, makeDefault: provider.key === defaultProvider }
    : { key: "", type: types[0] ?? "openai-chat", enabled: true, displayName: "", model: "", reasoningEffort: "", apiUrl: "", apiKeyEnv: "",
      apiKey: "", clearApiKey: false, makeDefault: false };
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
  return null;
}

/** The wire edit for a form: blank optional fields are sent as null, and an untouched secret is kept. */
export function providerEdit(form: ProviderForm): GlobalConfigProviderEdit {
  const optional = (value: string) => value.trim() ? value.trim() : null;
  return { key: form.key.trim().toLowerCase(), type: form.type, enabled: form.enabled, displayName: optional(form.displayName), model: optional(form.model),
    reasoningEffort: optional(form.reasoningEffort), apiUrl: optional(form.apiUrl), apiKeyEnv: optional(form.apiKeyEnv),
    apiKey: form.clearApiKey || !form.apiKey ? null : form.apiKey, clearApiKey: form.clearApiKey };
}

/** Provider types that sign in through their own account flow instead of an API key. */
export function usesAccountSignIn(type: string): boolean {
  return type === "codex" || type === "copilot" || type === "xai";
}
