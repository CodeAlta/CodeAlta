import type { GlobalConfigProvider, GlobalConfigProviderEdit } from "#neoastra";
import { brandColor } from "./brands";
import type { MessageKey } from "./localization";
import { validConfigDir } from "./claudeCli/claudeCli";
import { environmentEntries, environmentProblem, environmentText, pluginFieldValues, samePluginFieldValues, type PluginFieldValues } from "./pluginProviders/pluginProviderForm";

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
  /** For a provider that runs Claude Code: the folder of its account (CLAUDE_CONFIG_DIR); blank for the folder of Claude Code. */
  configDir: string;
  /**
   * For a provider that runs Claude Code: the variables its CLI starts with, one `NAME=value` per line, such as a token
   * of its account (CLAUDE_CODE_OAUTH_TOKEN), which a sandbox on macOS needs.
   */
  environment: string;
  /** The order of the models (`provider`, `name`, `name_version`); blank for the order of its type. */
  modelsOrder: string;
  /** The size of context, in input tokens, of the sessions whose model has it; null for the size of their model. */
  contextSize: number | null;
  /** For a provider whose type a plugin runs: the values of the settings of its type, by key. */
  plugin: PluginFieldValues;
}>;

/** The form for an existing provider, or a blank form for a new one. */
export function providerForm(provider: GlobalConfigProvider | null, defaultProvider: string | null, types: readonly string[]): ProviderForm {
  return provider
    ? { key: provider.key, type: provider.type, enabled: provider.enabled, displayName: provider.displayName ?? "", model: provider.model ?? "",
      reasoningEffort: provider.reasoningEffort ?? "", apiUrl: provider.apiUrl ?? "", apiKeyEnv: provider.apiKeyEnv ?? "", apiKey: "",
      clearApiKey: false, makeDefault: provider.key === defaultProvider, icon: provider.icon ?? "", color: provider.color ?? "",
      anthropicApiKey: provider.anthropicApiKey ?? "", permissionMode: provider.permissionMode ?? "",
      configDir: provider.configDir ?? "", environment: environmentText(provider.environment), modelsOrder: provider.modelsOrder ?? "",
      contextSize: provider.contextSize ?? null, plugin: pluginFieldValues(provider) }
    : { key: "", type: types[0] ?? "openai-chat", enabled: true, displayName: "", model: "", reasoningEffort: "", apiUrl: "", apiKeyEnv: "",
      apiKey: "", clearApiKey: false, makeDefault: false, icon: "", color: "", anthropicApiKey: "", permissionMode: "", configDir: "", environment: "", modelsOrder: "",
      contextSize: null, plugin: {} };
}

export function providerFormDirty(form: ProviderForm, baseline: ProviderForm): boolean {
  return (Object.keys(form) as (keyof ProviderForm)[]).some(field => field === "plugin" ? !samePluginFieldValues(form.plugin, baseline.plugin) : form[field] !== baseline[field]);
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
  if (runsOwnCli(form.type) && !validConfigDir(form.configDir)) return "A folder is a full path or starts with ~.";
  if (runsOwnCli(form.type)) return environmentProblem(form.environment);
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
    permissionMode: cli ? optional(form.permissionMode) : null,
    // So is the folder of its account.
    configDir: cli ? optional(form.configDir) : null,
    // And the variables its CLI starts with: an empty list removes them.
    environment: cli ? environmentEntries(form.environment) : null, modelsOrder: optional(form.modelsOrder), contextSize: form.contextSize };
}

/** The orders of the models of a provider, by their value in the configuration file. */
export const modelsOrders = ["provider", "name", "name_version"] as const;
const modelsOrderLabels: Readonly<Record<string, MessageKey>> = { provider: "Provider order", name: "By name", name_version: "By name, newest first" };

/** The name of an order of the models; a value of a newer version of CodeAlta is shown as it is. */
export function modelsOrderLabel(order: string, t: (key: MessageKey) => string): string {
  const key = modelsOrderLabels[order];
  return key ? t(key) : order;
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

/**
 * What to do about a provider that is not ready, when its test gave one of the reasons the host names. The text of a
 * signed-out provider takes its display name as `{provider}`: an account is signed out, not Claude Code itself.
 */
export function providerProblem(reason: string | null | undefined): MessageKey | null {
  // The command of the box starts Claude Code with the folder of the account of the provider.
  return reason === "claude-code-signed-out" ? "{provider} is not signed in. Run the command of the Claude Code CLI box in a terminal and type /login, then test again."
    : reason === "claude-code-not-found" ? "Claude Code was not found. Download the version CodeAlta is tested with in the form of the provider, or install Claude Code, then test again."
    : reason === "claude-code-unavailable" ? "Claude Code did not start or did not answer. Run claude in a terminal to check it, then test again."
    // An agent of a plugin (ACP): its form signs it in and says what it runs.
    : reason === "acp-signed-out" ? "{provider} is not signed in. Sign in from its form, then test again."
    : reason === "acp-not-installed" ? "The command of {provider} was not found. Install the agent, or the tool that runs it, then test again."
    : reason === "acp-unavailable" ? "{provider} did not start or did not answer. Check its command, then test again."
    // Codex CLI, a provider of a built-in plugin: CodeAlta downloads the app-server it runs.
    : reason === "codex-cli-signed-out" ? "{provider} is not signed in. Sign in from its form, then test again."
    : reason === "codex-cli-not-found" ? "{provider} could not find or download Codex. Check the connection, or the command in its form, then test again."
    : reason === "codex-cli-unavailable" ? "{provider} did not start or did not answer. Check its command, then test again."
    : null;
}

/**
 * What the form of a provider says of its state, above its fields, while it is not ready: a provider that waits for a
 * sign-in (a warning), one whose test named a cause, or one that failed for a cause the window has no words for (the log
 * keeps what it said). Null for a provider that works, is disabled, or was not tried yet.
 */
export function providerStateProblem(state: Readonly<{ availability: string; reason: string | null }> | undefined, enabled: boolean)
  : Readonly<{ key: MessageKey; intent: "warning" | "danger"; signIn: boolean }> | null {
  if (!enabled || !state) return null;
  if (state.reason === "acp-signed-out" || state.reason === "codex-cli-signed-out") return { key: "{provider} is not signed in: sign in below, then test it again.", intent: "warning", signIn: true };
  const known = providerProblem(state.reason);
  if (known) return { key: known, intent: isSignedOut(state.reason) ? "warning" : "danger", signIn: isSignedOut(state.reason) };
  return state.availability === "Failed" || state.availability === "Unsupported"
    ? { key: "{provider} did not start. Test it again; Settings > Application Logs says why.", intent: "danger", signIn: false }
    : null;
}

/** Whether a provider that is not ready only waits for its account to be signed in. */
export function isSignedOut(reason: string | null | undefined): boolean {
  return reason === "claude-code-signed-out" || reason === "acp-signed-out" || reason === "codex-cli-signed-out";
}
