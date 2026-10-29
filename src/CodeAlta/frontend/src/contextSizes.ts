import { compactTokens } from "./contextUsage";

/** Only these advertised endpoints authorize a session-scoped context choice. A maximum is not an endpoint. */
export type ContextModel = Readonly<{ contextSizes?: readonly number[] | null; defaultContextTokens?: number | null }>;
export type ContextChoice = Readonly<{ tokens: number | null }> | null | undefined;

export function contextSizes(model: ContextModel | null | undefined): number[] {
  return [...new Set((model?.contextSizes ?? []).filter(value => Number.isSafeInteger(value) && value > 0))].sort((a, b) => a - b);
}

export function validContext(choice: ContextChoice, model: ContextModel | null | undefined): boolean {
  if (choice == null || choice.tokens === null) return true;
  const tokens = choice.tokens;
  return Number.isSafeInteger(tokens) && tokens > 0 && model?.contextSizes?.includes(tokens) === true;
}

/** Formats the resolved Default capacity; explicit and unverified choices are rendered by the selector. */
export function defaultContextSummary(model: ContextModel | null | undefined): string | null {
  const tokens = model?.defaultContextTokens;
  return tokens != null && Number.isSafeInteger(tokens) && tokens > 0 ? compactTokens(String(tokens)) : null;
}

/** Preview only host-resolved input limits. An unknown or unverified choice has no draft usage meter. */
export function draftContextInputTokens(model: (ContextModel & {
  defaultInputTokens?: number | null;
  contextInputTokens?: Readonly<Record<string, number | null>> | null;
}) | null | undefined, choice: ContextChoice): number | null {
  if (!validContext(choice, model)) return null;
  const tokens = choice?.tokens == null ? model?.defaultInputTokens : model?.contextInputTokens?.[choice.tokens];
  return tokens != null && Number.isSafeInteger(tokens) && tokens > 0 ? tokens : null;
}

/** ACP's existing provider-global context option remains the only context control for that model. */
export function hasProviderContextOption(options: readonly Readonly<{ id: string; name: string }>[] | null | undefined): boolean {
  return options?.some(option => /context/i.test(option.id) || /\bcontext\b/i.test(option.name)) ?? false;
}
