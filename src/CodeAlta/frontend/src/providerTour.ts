import type { MessageKey } from "./localization";
import { usesAccountSignIn } from "./providerForm";

/** Where the tour is told it was already shown, so it starts by itself only once in this window's profile. */
export const providerTourStorageKey = "codealta.desktop.providerTour.v1";

type TourProvider = Readonly<{ key: string; type: string; effectiveName: string; enabled: boolean; apiKeyEnv: string | null; defaults: Readonly<{ apiKeyEnv: string | null }> }>;

/** One stop of the provider tour: what it points at and what it says. */
export type ProviderTourStep = Readonly<{
  id: "subscription" | "sign-in" | "others";
  /** `provider` and `account` belong to the provider named by {@link provider}; `list` is the whole list. */
  target: "provider" | "account" | "list";
  /** The provider selected while this step shows, or null to leave the selection alone. */
  provider: string | null;
  title: MessageKey; body: MessageKey; parameters: Readonly<Record<string, string>>;
}>;

/**
 * The stops for a set of configured providers: the one that signs in with an account (Codex and its ChatGPT
 * plan first), its sign-in, then the providers that take an API key. Without an account provider only the
 * last stop remains.
 */
export function providerTourSteps(providers: readonly TourProvider[]): readonly ProviderTourStep[] {
  const account = providers.find(provider => provider.type === "codex") ?? providers.find(provider => usesAccountSignIn(provider.type)) ?? null;
  // The example is a provider most people know, when it is configured.
  const withVariable = providers.filter(provider => !usesAccountSignIn(provider.type) && (provider.apiKeyEnv ?? provider.defaults.apiKeyEnv));
  const keyed = withVariable.find(provider => provider.key === "anthropic") ?? withVariable.find(provider => provider.key === "openai") ?? withVariable[0];
  const variable = keyed ? keyed.apiKeyEnv ?? keyed.defaults.apiKeyEnv ?? "" : "";
  const others: ProviderTourStep = { id: "others", target: "list", provider: null,
    title: account ? "Or use another provider" : "Set up a provider",
    body: variable ? "The other providers take an API key, best given through the environment variable their form names, such as {variable}. Select one, enable it, then Save and apply."
      : "The other providers take an API key, best given through an environment variable. Select one, enable it, then Save and apply.",
    parameters: { variable } };
  if (!account) return [others];
  const name = account.effectiveName;
  // Codex and Copilot both sign in with a subscription; the tour names the two and continues with the first.
  const other = providers.find(provider => provider !== account && (provider.type === "codex" || provider.type === "copilot")) ?? null;
  return [
    { id: "subscription", target: "provider", provider: account.key, title: "Start with a subscription you already have",
      parameters: { name, plan: subscription(account.type), other: other?.effectiveName ?? "", otherPlan: other ? subscription(other.type) : "" },
      body: other ? "No model provider is enabled yet. {name} ({plan}) and {other} ({otherPlan}) sign in with a subscription you already have: there is no API key to create. This guide continues with {name}."
        : "No model provider is enabled yet. {name} signs in with your {plan} subscription: there is no API key to create." },
    { id: "sign-in", target: "account", provider: account.key, parameters: { name },
      title: "Sign in with the browser",
      body: "The sign-in page opens in your browser. Once it completes, {name} is enabled and ready for your first session." },
    others,
  ];
}

/** The subscription an account provider signs in with; a product name, the same in every language. */
function subscription(type: string): string {
  return type === "codex" ? "ChatGPT" : type === "copilot" ? "GitHub Copilot" : type === "xai" ? "xAI" : type;
}

/** Whether the tour starts by itself: nothing is enabled, and it was not shown before. */
export function startsProviderTour(providers: readonly Pick<TourProvider, "enabled">[], shownBefore: boolean): boolean {
  return !shownBefore && !providers.some(provider => provider.enabled);
}
