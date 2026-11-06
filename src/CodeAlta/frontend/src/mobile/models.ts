import type { ModelOption, Options, TierOption } from "./contract";
import { sectionModels } from "../modelOrder";

/** A variant a model id may carry. */
export type Variant = "thinking" | "fast";
const variantWords: readonly Variant[] = ["thinking", "fast"];
export const variantLabels: Readonly<Record<Variant, string>> = { thinking: "Thinking", fast: "Fast" };

/** Ids that differ only by their variants, as one entry of the list with options. */
export type ModelGroup<T> = Readonly<{
  entry: T;
  members: readonly Readonly<{ model: T; flags: readonly Variant[] }>[];
  /** The variants the entry itself has (when there is no model without them). */
  fixed: readonly Variant[];
  /** The variants that can be turned on or off. */
  options: readonly Variant[];
  /** The model with these variants on, besides the fixed ones. */
  find(chosen: readonly Variant[]): T | undefined;
}>;

// The flags of an id are the variant words it has; the key is the id without them. The entry is the base id, or the
// id with the fewest flags when no base exists (then its own flags are part of the entry and not options).
export function groupVariants<T extends { id: string }>(models: readonly T[]): ModelGroup<T>[] {
  const groups = new Map<string, { model: T; flags: Variant[] }[]>();
  for (const model of models) {
    const words = model.id.split("-");
    const flags = words.filter((word): word is Variant => (variantWords as readonly string[]).includes(word)).sort();
    const key = words.filter(word => !(variantWords as readonly string[]).includes(word)).join("-");
    if (!groups.has(key)) groups.set(key, []);
    groups.get(key)!.push({ model, flags });
  }
  return [...groups.values()].map(members => {
    members.sort((a, b) => a.flags.length - b.flags.length);
    const fixed = members[0].flags, entry = members[0].model;
    const options = variantWords.filter(flag => !fixed.includes(flag) && members.some(x => x.flags.includes(flag)));
    const find = (chosen: readonly Variant[]) => members.find(x => x.flags.join() === [...fixed, ...chosen].sort().join())?.model;
    return { entry, members, fixed, options, find };
  });
}

/** The name of an entry of the list, without the "No Thinking" of its base model. */
export const entryName = (model: ModelOption): string => model.name.replace(/\s+No Thinking\b/i, "");

/** What a new session is started with: a tier of the settings, or a provider, a model and an effort. */
export type Choice = Readonly<{ tier: string | null; provider: string; model: string; effort: string }>;

/** What is remembered of the choices made for a project, as the desktop's `codealta.desktop.newSessionPreferences.v1`. */
export type ProjectPreferences = Readonly<{ agentPromptId?: string; providerKey?: string; modelId?: string; reasoningEffort?: string; tier?: string; permissionMode?: string }>;

export const tierLabels: Readonly<Record<string, string>> = { complex: "Complex", standard: "Standard", quick: "Quick" };
export const tierLabel = (tier: string): string => tierLabels[tier] ?? tier;

export const modelsOf = (options: Options, provider: string): ModelOption[] => options.models.filter(model => model.provider === provider);
export const findModel = (options: Options, provider: string | null, id: string | null): ModelOption | undefined =>
  options.models.find(model => model.provider === provider && model.id === id);
export const providerName = (options: Options, provider: string): string => options.models.find(model => model.provider === provider)?.providerName || provider;
export const providers = (options: Options): string[] => [...new Set(options.models.map(model => model.provider))];

/** A model of the provider, always one it lists: the wanted one, else the configured default when it is this provider's, else the first it shows. */
export function pickModel(options: Options, provider: string, wanted?: string | null): ModelOption | undefined {
  const models = modelsOf(options, provider), defaults = options.defaults;
  return models.find(model => model.id === wanted)
    ?? (defaults?.provider === provider ? models.find(model => model.id === defaults.model) : undefined)
    ?? sectionModels(groupVariants(models).map(group => group.entry), options.modelsOrders?.[provider]).frontier[0] ?? models[0];
}

/** The effort of a model: the wanted one, else the effort of the settings when it is their model, else the model's default, else its first. */
export function pickEffort(options: Options, model: ModelOption | undefined, wanted?: string | null): string {
  const efforts = model?.efforts ?? [], defaults = options.defaults;
  const configured = defaults && model && defaults.provider === model.provider && defaults.model === model.id ? defaults.reasoning : null;
  return [wanted, configured, model?.defaultEffort].find(effort => effort && efforts.includes(effort)) ?? efforts[0] ?? "";
}

/** Whether a tier can be chosen: it has a model and no error. */
export const tierReady = (tier: TierOption): boolean => !!tier.provider && !!tier.model && !tier.error;

/** What a tier says in the list: its model by name, or why it cannot be chosen. */
export function tierText(options: Options, tier: TierOption): string {
  if (tier.error) return tier.error;
  if (!tier.provider || !tier.model) return "No model ready";
  return findModel(options, tier.provider, tier.model)?.name ?? tier.model;
}

/**
 * The choice of a tier: its own provider, model and effort, never another model in its place (the server starts the
 * session with the tier, which it resolves itself). Null when the tier cannot be chosen.
 */
export function tierChoice(options: Options, name: string): Choice | null {
  const tier = options.tiers.find(t => t.tier === name);
  if (!tier || !tierReady(tier)) return null;
  const model = findModel(options, tier.provider, tier.model);
  // The tier's own effort, shown as the server will use it.
  const effort = tier.reasoning || (model ? pickEffort(options, model) : "");
  return { tier: name, provider: tier.provider!, model: tier.model!, effort };
}

/**
 * The first choice of the form for a project: the tier it was left on (while that tier still has the same model), the
 * default tier of the settings when nothing was chosen for the project, else the provider, model and effort it was left
 * on, each only while still offered. Also returns what is still valid of the remembered choices, so that a pick no
 * longer offered is forgotten instead of kept to fail again.
 */
export function initialChoice(options: Options, stored: ProjectPreferences): { choice: Choice; remembered: ProjectPreferences } {
  // Empty values (null, "") are no pick: they are dropped, and so forgotten below.
  const saved = Object.fromEntries(Object.entries(stored).filter(([, value]) => typeof value === "string" && value)) as ProjectPreferences;
  if (saved.tier) {
    const tier = options.tiers.find(t => t.tier === saved.tier);
    const choice = tier && tier.provider === saved.providerKey && tier.model === saved.modelId ? tierChoice(options, saved.tier) : null;
    if (choice) return { choice, remembered: saved };
  }
  if (!saved.providerKey && options.defaultTier) {
    const choice = tierChoice(options, options.defaultTier);
    if (choice) return { choice, remembered: forget(saved, ["tier"]) };
  }
  // Only a provider that lists models now: the default provider may have none (one that is not ready).
  const offered = providers(options);
  const provider = saved.providerKey && offered.includes(saved.providerKey) ? saved.providerKey
    : options.defaults?.provider && offered.includes(options.defaults.provider) ? options.defaults.provider : offered[0] ?? "";
  const model = pickModel(options, provider, saved.providerKey === provider ? saved.modelId : undefined);
  const effort = pickEffort(options, model, saved.reasoningEffort);
  const choice: Choice = { tier: null, provider, model: model?.id ?? "", effort };
  let remembered = forget(saved, ["tier"]);
  if (saved.providerKey && saved.providerKey !== choice.provider) remembered = forget(remembered, ["providerKey", "modelId", "reasoningEffort", "permissionMode"]);
  if (saved.modelId && saved.modelId !== choice.model) remembered = forget(remembered, ["modelId", "reasoningEffort"]);
  if (saved.reasoningEffort && saved.reasoningEffort !== choice.effort) remembered = forget(remembered, ["reasoningEffort"]);
  return { choice, remembered };
}

function forget(preferences: ProjectPreferences, keys: readonly (keyof ProjectPreferences)[]): ProjectPreferences {
  const copy: Record<string, string | undefined> = { ...preferences };
  for (const key of keys) delete copy[key];
  return copy as ProjectPreferences;
}

/** What is remembered of a choice, with the permission mode chosen for its provider. */
export function rememberChoice(agentPromptId: string | undefined, choice: Choice, permissionMode?: string): ProjectPreferences {
  const preferences: Record<string, string> = {};
  if (agentPromptId) preferences.agentPromptId = agentPromptId;
  if (choice.provider) preferences.providerKey = choice.provider;
  if (choice.model) preferences.modelId = choice.model;
  if (choice.effort) preferences.reasoningEffort = choice.effort;
  if (choice.tier) preferences.tier = choice.tier;
  if (permissionMode && choice.provider) preferences.permissionMode = permissionMode;
  return preferences;
}

/** The remembered choices without their permission mode (one the provider no longer offers). */
export const forgetMode = (preferences: ProjectPreferences): ProjectPreferences => forget(preferences, ["permissionMode"]);

/** What the model button says: the tier and its model, or the model's entry name. */
export function choiceLabel(options: Options, choice: Choice): string {
  const model = findModel(options, choice.provider, choice.model);
  const group = groupVariants(modelsOf(options, choice.provider)).find(g => g.members.some(x => x.model.id === choice.model));
  const name = (group && entryName(group.entry)) || model?.name || choice.model || "No model";
  return choice.tier ? `${tierLabel(choice.tier)} · ${name}` : name;
}

/** The request of a choice: the tier alone, or the provider, the model and the effort. */
export function choiceRequest(choice: Choice): { tier: string } | { provider: string; model: string; reasoning?: string } {
  return choice.tier ? { tier: choice.tier } : { provider: choice.provider, model: choice.model, ...(choice.effort ? { reasoning: choice.effort } : {}) };
}

/** The remembered choices of every project, under one key of the storage. */
export const preferencesKey = "codealta.mobile.newSessionPreferences.v1";

/** The project a new session was last started in or chosen for, under one key of the storage. */
export const lastProjectKey = "codealta.mobile.lastProject.v1";