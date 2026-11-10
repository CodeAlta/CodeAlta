import { useEffect, useSyncExternalStore } from "react";
import { globalConfig, type GlobalConfigModelTiersResponse, type ModelFallbackSetting, type ModelTierChoiceItem, type ModelTierList } from "#neoastra";
import { keepIfSame } from "./keepIfSame";

// The tiers of models of the settings: complex, standard and quick, each an ordered list of targets (a model of a
// configured provider with its reasoning efforts in order). A session asks for a tier and runs on its first ready model;
// with the fallback on, it goes on with the next one when its provider fails.

export type TierName = "complex" | "standard" | "quick";
export const tierNames: readonly TierName[] = Object.freeze(["complex", "standard", "quick"]);
export const tierLabels: Readonly<Record<TierName, "Complex" | "Standard" | "Quick">> = Object.freeze({ complex: "Complex", standard: "Standard", quick: "Quick" });

/** A target of a tier. Efforts are written as the host names them (Low, Medium, High, XHigh…). */
export type TierTarget = Readonly<{ providerKey: string; modelId: string; reasoning: readonly string[] }>;

/** The tiers as the settings hold them, with the model each gives now and the fallback. */
export type TierState = Readonly<{
  tiers: Readonly<Record<TierName, readonly TierTarget[]>>;
  choices: Readonly<Record<TierName, ModelTierChoiceItem | null>>;
  fallback: ModelFallbackSetting;
  /** Where the tiers come from: the space's own, or the global ones. */
  source: "global" | "space";
  /** How CodeAlta runs the turns of a session on Adaptive: its levels; null while it is not set up. */
  adaptive: AdaptiveChoice | null;
  /** Whether a session can be put on Adaptive: it is set up with a level. */
  adaptiveReady: boolean;
  /** The levels Adaptive proposes for the providers that are ready; null when it knows none of their models. */
  adaptiveProposal: readonly AdaptiveLevel[] | null;
}>;

/** The levels of Adaptive, from the cheapest. */
export type AdaptiveLevelName = "low" | "medium" | "high" | "max";
export const adaptiveLevelNames: readonly AdaptiveLevelName[] = Object.freeze(["low", "medium", "high", "max"]);
export const adaptiveLevelLabels: Readonly<Record<AdaptiveLevelName, "Low" | "Medium" | "High" | "Max">> = Object.freeze({ low: "Low", medium: "Medium", high: "High", max: "Max" });

/**
 * A level of Adaptive: the model of its turns and the critics that review its changes of code (none for a recheck), with
 * the names of its model while its provider is ready.
 */
export type AdaptiveLevel = Readonly<{ level: AdaptiveLevelName; model: TierTarget; critics: readonly TierTarget[]; choice: ModelTierChoiceItem | null;
  /** The names of the critics as their providers give them; absent for a level the window changed. */
  criticNames?: readonly string[] }>;

/** Adaptive, as Settings > Models sets it up: the level a session takes when none is chosen, and the levels that are set. */
export type AdaptiveChoice = Readonly<{ level: AdaptiveLevelName; levels: readonly AdaptiveLevel[] }>;

/** The most critics of a level. */
export const maximumAdaptiveCritics = 3;

/** The name the composer and the model list give the choice of Adaptive, beside the tiers. */
export const adaptiveTier = "adaptive";

export const maximumTierTargets = 16;
/** How many times in a row a session resumes by itself after a limit, unless the settings say otherwise; and the most they accept. */
export const defaultMaxResumes = 5;
export const maximumMaxResumes = 50;

/** A number of resumes in a row the host accepts: a whole number from 1 to 50, the default for anything else. */
export function readMaxResumes(value: unknown): number {
  return typeof value === "number" && Number.isInteger(value) && value >= 1 && value <= maximumMaxResumes ? value : defaultMaxResumes;
}
const maximumEfforts = 8;
const text = (value: unknown, limit = 256): value is string => typeof value === "string" && value.trim().length > 0 && value.length <= limit;

export const emptyTiers: TierState = Object.freeze({
  tiers: Object.freeze({ complex: [], standard: [], quick: [] }),
  choices: Object.freeze({ complex: null, standard: null, quick: null }),
  fallback: Object.freeze({ enabled: false, onQuota: true, onUnavailable: true, resume: false, maxResumes: defaultMaxResumes }),
  source: "global",
  adaptive: null,
  adaptiveReady: false,
  adaptiveProposal: null,
});

/** Whether the user set no tier: the composer then offers none and new sessions keep the default provider. */
export const noTiers = (state: TierState | null | undefined) => !state || tierNames.every(tier => state.tiers[tier].length === 0);

/** The tiers a reply of the host gives; null for one that is not such a reading. A tier the host did not list is empty. */
export function readTierState(reply: Pick<GlobalConfigModelTiersResponse, "tiers" | "fallback"> & Partial<Pick<GlobalConfigModelTiersResponse, "source" | "adaptive" | "adaptiveReady" | "adaptiveProposal">> | null | undefined): TierState | null {
  if (!reply || !Array.isArray(reply.tiers) || !reply.fallback) return null;
  const tiers: Record<TierName, TierTarget[]> = { complex: [], standard: [], quick: [] };
  const choices: Record<TierName, ModelTierChoiceItem | null> = { complex: null, standard: null, quick: null };
  for (const list of reply.tiers) {
    const tier = tierNames.find(name => name === list?.tier);
    if (!tier || !Array.isArray(list.targets)) return null;
    for (const target of list.targets.slice(0, maximumTierTargets)) {
      if (!text(target?.providerKey) || !text(target.modelId) || !Array.isArray(target.reasoning)) return null;
      tiers[tier].push({ providerKey: target.providerKey, modelId: target.modelId, reasoning: target.reasoning.filter((effort: unknown): effort is string => text(effort, 32)).slice(0, maximumEfforts) });
    }
    const choice = list.choice;
    choices[tier] = choice && text(choice.providerKey) && text(choice.modelId) ? choice : null;
  }
  return { tiers, choices, fallback: { enabled: reply.fallback.enabled === true, onQuota: reply.fallback.onQuota !== false, onUnavailable: reply.fallback.onUnavailable !== false,
    resume: reply.fallback.resume === true, maxResumes: readMaxResumes(reply.fallback.maxResumes) },
    source: reply.source === "space" ? "space" : "global", ...readAdaptive(reply.adaptive, reply.adaptiveReady),
    adaptiveProposal: readLevels(reply.adaptiveProposal) };
}

const readLevelName = (value: unknown): AdaptiveLevelName | undefined => adaptiveLevelNames.find(name => name === value);

function readTarget(value: unknown): TierTarget | null {
  const target = value as Partial<Record<"providerKey" | "modelId" | "reasoning", unknown>> | null | undefined;
  if (!text(target?.providerKey) || !text(target?.modelId)) return null;
  const reasoning = Array.isArray(target.reasoning) ? target.reasoning.filter((effort: unknown): effort is string => text(effort, 32)).slice(0, 1) : [];
  return { providerKey: target.providerKey, modelId: target.modelId, reasoning };
}

// The levels a reply gives, from the cheapest; a level that is not one, or without a model, is left out.
function readLevels(value: unknown): readonly AdaptiveLevel[] | null {
  if (!Array.isArray(value)) return null;
  const levels: AdaptiveLevel[] = [];
  for (const item of value.slice(0, adaptiveLevelNames.length)) {
    const level = readLevelName(item?.level), model = readTarget(item?.model);
    if (!level || !model || levels.some(existing => existing.level === level)) continue;
    const critics = Array.isArray(item.critics) ? item.critics.map(readTarget).filter((critic: TierTarget | null): critic is TierTarget => !!critic).slice(0, maximumAdaptiveCritics) : [];
    const choice = item.choice && text(item.choice.providerKey) && text(item.choice.modelId) ? item.choice as ModelTierChoiceItem : null;
    const names = Array.isArray(item.criticNames) && item.criticNames.length === critics.length && item.criticNames.every((name: unknown) => text(name))
      ? item.criticNames as string[] : undefined;
    levels.push(names ? { level, model, critics, choice, criticNames: names } : { level, model, critics, choice });
  }
  return levels.sort((left, right) => adaptiveLevelNames.indexOf(left.level) - adaptiveLevelNames.indexOf(right.level));
}

// Adaptive as a reply gives it: ready only with a level.
function readAdaptive(value: unknown, ready: unknown): Pick<TierState, "adaptive" | "adaptiveReady"> {
  const read = value as Partial<Record<"level" | "levels", unknown>> | null | undefined;
  if (!read) return { adaptive: null, adaptiveReady: false };
  const levels = readLevels(read.levels) ?? [];
  return { adaptive: { level: readLevelName(read.level) ?? "medium", levels }, adaptiveReady: levels.length > 0 && ready === true };
}

/** The level a session runs on, as the host resolves it: the one asked when it is set, else the default, else the nearest one (the cheaper on a tie). */
export function adaptiveLevelOf(adaptive: AdaptiveChoice | null | undefined, asked?: string | null): AdaptiveLevel | null {
  if (!adaptive || adaptive.levels.length === 0) return null;
  const wanted = readLevelName(asked) ?? adaptive.level;
  const index = adaptiveLevelNames.indexOf(wanted);
  return adaptive.levels.find(level => level.level === wanted)
    ?? [...adaptive.levels].sort((left, right) => Math.abs(adaptiveLevelNames.indexOf(left.level) - index) - Math.abs(adaptiveLevelNames.indexOf(right.level) - index)
      || adaptiveLevelNames.indexOf(left.level) - adaptiveLevelNames.indexOf(right.level))[0];
}

/** The model a session on Adaptive starts its turns with at a level: the one of that level; null while it cannot be chosen. */
export function adaptiveChoice(state: TierState | null | undefined, level?: string | null): ModelTierChoiceItem | null {
  return state?.adaptive && state.adaptiveReady ? adaptiveLevelOf(state.adaptive, level)?.choice ?? null : null;
}

/** The name the draft of a new session gives Adaptive at a level, beside the tiers: `adaptive` for the default level. */
export const adaptiveTierOf = (level?: string | null) => readLevelName(level) ? `${adaptiveTier}:${level}` : adaptiveTier;

/** Whether a tier of a draft or a session is Adaptive, and its level when it names one. */
export function readAdaptiveTier(tier: string | null | undefined): Readonly<{ adaptive: boolean; level: AdaptiveLevelName | null }> {
  if (tier === adaptiveTier) return { adaptive: true, level: null };
  const level = tier?.startsWith(`${adaptiveTier}:`) ? readLevelName(tier.slice(adaptiveTier.length + 1)) : undefined;
  return level ? { adaptive: true, level } : { adaptive: false, level: null };
}

const modelOfTarget = (target: TierTarget) => target.modelId;

/** What a level does, in a few words: its model, and who reviews its changes (or its recheck). */
export function adaptiveLevelSummary(level: AdaptiveLevel, t: (key: "Recheck" | "Review by {models}", values?: Record<string, string>) => string): string {
  const model = level.choice?.modelName ?? modelOfTarget(level.model);
  const effort = level.choice?.reasoning ?? level.model.reasoning[0];
  const critics = level.criticNames ?? level.critics.map(modelOfTarget);
  const review = critics.length > 0 ? t("Review by {models}", { models: critics.join(", ") }) : t("Recheck");
  return [effort ? `${model} ${effort}` : model, review].join(" · ");
}

/**
 * Adaptive as the list of the composer offers it, after the tiers: each level with what it runs; `ready` false while
 * it is not set up, when choosing it offers to set it up.
 */
export function composerAdaptiveEntry(state: TierState | null | undefined,
  t: (key: "Not set up" | "No model ready" | "Recheck" | "Review by {models}" | "Low" | "Medium" | "High" | "Max", values?: Record<string, string>) => string)
  : Readonly<{ ready: boolean; detail: string; title: string | null; levels: readonly Readonly<{ level: AdaptiveLevelName; label: string; detail: string | null; title: string | null }>[] }> {
  if (!state?.adaptive || !state.adaptiveReady) return { ready: false, detail: t("Not set up"), title: null, levels: [] };
  const levels = state.adaptive.levels.map(level => {
    const summary = adaptiveLevelSummary(level, t);
    // Set up, while the provider of its model is not ready yet (a provider that starts).
    return { level: level.level, label: t(adaptiveLevelLabels[level.level]), detail: level.choice ? summary : null,
      title: level.choice ? `${level.choice.providerName} · ${summary}` : null };
  });
  const usual = adaptiveLevelOf(state.adaptive);
  const detail = usual?.choice ? adaptiveLevelSummary(usual, t) : t("No model ready");
  return { ready: true, detail, title: usual?.choice ? `${usual.choice.providerName} · ${detail}` : null, levels };
}

/** The tiers as the host saves them. */
export function tierLists(state: TierState): ModelTierList[] {
  return tierNames.map(tier => ({ tier, targets: state.tiers[tier].map(target => ({ ...target, reasoning: [...target.reasoning] })), choice: null }));
}

const sameModel = (left: TierTarget, right: TierTarget) =>
  left.providerKey.toLowerCase() === right.providerKey.toLowerCase() && left.modelId.toLowerCase() === right.modelId.toLowerCase();

function withTier(state: TierState, tier: TierName, targets: readonly TierTarget[]): TierState {
  return { ...state, tiers: { ...state.tiers, [tier]: targets } };
}

/** Adds a model at the end of a tier; null when the tier has it already or is full. */
export function addTierTarget(state: TierState, tier: TierName, target: TierTarget): TierState | null {
  const targets = state.tiers[tier];
  if (targets.length >= maximumTierTargets || targets.some(existing => sameModel(existing, target))) return null;
  return withTier(state, tier, [...targets, target]);
}

/** Removes the target at an index of a tier. */
export function removeTierTarget(state: TierState, tier: TierName, index: number): TierState {
  return withTier(state, tier, state.tiers[tier].filter((_, at) => at !== index));
}

/**
 * Moves a target before the one at `to.index` (the end for the length of the tier), in its tier or into another one;
 * null for a move that changes nothing or that the other tier refuses (it has the model already, or is full).
 */
export function moveTierTarget(state: TierState, from: Readonly<{ tier: TierName; index: number }>, to: Readonly<{ tier: TierName; index: number }>): TierState | null {
  const source = state.tiers[from.tier];
  const moved = source[from.index];
  if (!moved) return null;
  if (from.tier === to.tier) {
    const index = Math.max(0, Math.min(to.index, source.length));
    if (index === from.index || index === from.index + 1) return null;
    const rest = source.filter((_, at) => at !== from.index);
    const at = index > from.index ? index - 1 : index;
    return withTier(state, from.tier, [...rest.slice(0, at), moved, ...rest.slice(at)]);
  }

  const target = state.tiers[to.tier];
  if (target.length >= maximumTierTargets || target.some(existing => sameModel(existing, moved))) return null;
  const index = Math.max(0, Math.min(to.index, target.length));
  const removed = withTier(state, from.tier, source.filter((_, at) => at !== from.index));
  return withTier(removed, to.tier, [...target.slice(0, index), moved, ...target.slice(index)]);
}

/**
 * Changes the model of the target at an index of a tier, in its place. The target keeps the efforts it has that the new
 * model offers, in its order, or starts with those of `startingEfforts` when none remain. Null when the tier has that
 * model already, the target itself included, or has no target at the index.
 */
export function replaceTierTarget(state: TierState, tier: TierName, index: number, target: Readonly<{ providerKey: string; modelId: string }>,
  model: Readonly<{ efforts: readonly string[]; defaultEffort: string | null }> | undefined): TierState | null {
  const targets = state.tiers[tier];
  const replaced = targets[index];
  if (!replaced || targets.some(existing => sameModel(existing, { ...target, reasoning: [] }))) return null;
  const offered = model?.efforts ?? [];
  const kept = replaced.reasoning.map(effort => offered.find(value => value.toLowerCase() === effort.toLowerCase())).filter((effort): effort is string => effort !== undefined);
  const reasoning = kept.length > 0 ? [...new Set(kept)] : startingEfforts(model);
  return withTier(state, tier, targets.map((existing, at) => at === index ? { providerKey: target.providerKey, modelId: target.modelId, reasoning } : existing));
}

/** Gives the target at an index of a tier its reasoning efforts, in order. */
export function setTierReasoning(state: TierState, tier: TierName, index: number, reasoning: readonly string[]): TierState {
  return withTier(state, tier, state.tiers[tier].map((target, at) => at === index ? { ...target, reasoning: [...new Set(reasoning)].slice(0, maximumEfforts) } : target));
}

/** Moves the effort at `from` before the one at `to` (the end for the length of the list). */
export function moveEffort(efforts: readonly string[], from: number, to: number): readonly string[] {
  const moved = efforts[from];
  if (moved === undefined) return efforts;
  const rest = efforts.filter((_, at) => at !== from);
  const at = Math.max(0, Math.min(to > from ? to - 1 : to, rest.length));
  return [...rest.slice(0, at), moved, ...rest.slice(at)];
}

/** The effort a model starts a target with: its default when it says one, then High, then its first; none for a model without. */
export function startingEfforts(model: Readonly<{ efforts: readonly string[]; defaultEffort: string | null }> | undefined): readonly string[] {
  if (!model || model.efforts.length === 0) return [];
  const supported = (effort: string | null) => effort ? model.efforts.find(value => value.toLowerCase() === effort.toLowerCase()) : undefined;
  return [supported(model.defaultEffort) ?? supported("High") ?? model.efforts[0]];
}

/**
 * The tiers as the list of the composer offers them, with the model each gives now; none when the user set no tier.
 * A tier whose models are none of them ready is offered without a model.
 */
export function composerTierEntries(state: TierState | null | undefined, t: (key: "Complex" | "Standard" | "Quick") => string)
  : readonly Readonly<{ tier: TierName; label: string; detail: string | null; title: string | null }>[] | undefined {
  if (!state || noTiers(state)) return undefined;
  return tierNames.filter(tier => state.tiers[tier].length > 0).map(tier => {
    const choice = state.choices[tier];
    return {
      tier,
      label: t(tierLabels[tier]),
      detail: choice ? `${choice.modelName}${choice.reasoning ? ` · ${choice.reasoning}` : ""}` : null,
      title: choice ? `${choice.providerName} · ${choice.modelName}${choice.reasoning ? ` · ${choice.reasoning}` : ""}` : null,
    };
  });
}

/** Whose tiers are read: the global ones, those of a space, or those of the space of a session. */
export type TierScope = Readonly<{ spaceId?: string | null; sessionId?: string | null }>;

type Read = (request: { expectedEpoch: string; spaceId: string | null; sessionId: string | null },
  options: { signal?: AbortSignal; timeoutMilliseconds?: number }) => Promise<GlobalConfigModelTiersResponse>;

const scopeKey = (epoch: string, scope?: TierScope | null) => `${epoch}|${scope?.spaceId ?? ""}|${scope?.sessionId ?? ""}`;

/** How a store reads again the tiers of a scope whose providers are still prepared: after `delay`, `reads` times at most in a row. */
export type PendingRereads = Readonly<{ delay: number; reads: number; schedule: (run: () => void, delay: number) => void }>;

// Every 2 seconds for 3 minutes at most: a provider that takes longer is read again once the user changes a provider.
const pendingRereads: PendingRereads = { delay: 2000, reads: 90, schedule: (run, delay) => { setTimeout(run, delay); } };

/**
 * The tiers of the window, read from the host for each scope: Settings, the page of a space and the composers show
 * the same readings, and a save tells every one of them, after reading again the scopes it may change. A reading the
 * host says is pending (a provider of its models is still prepared, as when the window starts) is made again until it
 * is not, so that a tier does not stay without a model (#367).
 */
export function createTierStore(read: Read, rereads: PendingRereads = pendingRereads) {
  let readings: ReadonlyMap<string, TierState> = new Map();
  const scopes = new Map<string, Readonly<{ epoch: string; scope: TierScope | null }>>();
  const loading = new Set<string>();
  // The pending readings made in a row for each scope, and the scopes whose next reading is planned.
  const pending = new Map<string, number>();
  const planned = new Set<string>();
  const listeners = new Set<() => void>();
  const tell = () => { for (const listener of [...listeners]) listener(); };
  // A reading that says what the window already has changes nothing: the composers read their scope each time a session is
  // shown, and none of them has to render again for it.
  const put = (key: string, state: TierState) => {
    const known = readings.get(key);
    if (known && keepIfSame(known, state) === known) return;
    readings = new Map(readings).set(key, state);
    tell();
  };
  async function refresh(epoch: string, scope: TierScope | null = null) {
    const key = scopeKey(epoch, scope);
    scopes.set(key, { epoch, scope });
    if (loading.has(key)) return;
    loading.add(key);
    try {
      const reply = await read({ expectedEpoch: epoch, spaceId: scope?.spaceId ?? null, sessionId: scope?.sessionId ?? null },
        { timeoutMilliseconds: 15_000 });
      const state = reply.status === "ok" || reply.status === "invalid" ? readTierState(reply) : null;
      if (state) put(key, state);
      if (reply.status !== "ok" || reply.pending !== true) pending.delete(key);
      else planAgain(key, epoch, scope);
    } catch {
      // The reading the window has stays; one made while the providers are prepared is tried again.
      if (pending.has(key)) planAgain(key, epoch, scope);
    }
    finally { loading.delete(key); }
  }
  // Plans the next reading of a scope whose providers are prepared, `reads` in a row at most. Past the last one, the count
  // starts again with the next reading asked from outside (a change of the providers).
  function planAgain(key: string, epoch: string, scope: TierScope | null) {
    const count = (pending.get(key) ?? 0) + 1;
    if (count > rereads.reads) { pending.delete(key); return; }
    pending.set(key, count);
    if (planned.has(key)) return;
    planned.add(key);
    rereads.schedule(() => { planned.delete(key); void refresh(epoch, scope); }, rereads.delay);
  }
  return Object.freeze({
    /** The readings, by scope; a new map each time one changes. */
    get: () => readings,
    /** The reading of a scope, or undefined until it is read. */
    of: (epoch: string, scope?: TierScope | null) => readings.get(scopeKey(epoch, scope)),
    /** Reads the tiers of a scope again from the host. */
    refresh,
    /** Reads again every scope of a host the window read, once its providers changed what the tiers give. */
    async refreshAll(epoch: string) {
      await Promise.all([...scopes.values()].filter(known => known.epoch === epoch).map(known => refresh(known.epoch, known.scope)));
    },
    /** What a save answered, for its scope; the other scopes of the host are read again, as the save may change them. */
    set(epoch: string, state: TierState, scope: TierScope | null = null) {
      const key = scopeKey(epoch, scope);
      put(key, state);
      for (const [other, known] of scopes) if (other !== key && known.epoch === epoch) void refresh(known.epoch, known.scope);
    },
    subscribe(listener: () => void) { listeners.add(listener); return () => { listeners.delete(listener); }; },
  });
}

export type TierStore = ReturnType<typeof createTierStore>;

/** The tiers of this window. */
export const tierStore = createTierStore((request, options) => globalConfig.modelTiers(request, options));

/**
 * The tiers of a host for a scope (the global ones without one), read when the host or the scope changes; null until
 * they are read.
 */
export function useTiers(epoch: string | null | undefined, scope: TierScope | null = null, store: TierStore = tierStore): TierState | null {
  const readings = useSyncExternalStore(store.subscribe, store.get, store.get);
  const spaceId = scope?.spaceId ?? null, sessionId = scope?.sessionId ?? null;
  useEffect(() => { if (epoch) void store.refresh(epoch, spaceId || sessionId ? { spaceId, sessionId } : null); }, [epoch, spaceId, sessionId, store]);
  return epoch ? readings.get(scopeKey(epoch, spaceId || sessionId ? { spaceId, sessionId } : null)) ?? null : null;
}