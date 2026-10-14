import { useEffect, useSyncExternalStore } from "react";
import { globalConfig, type GlobalConfigModelTiersResponse, type ModelFallbackSetting, type ModelTierChoiceItem, type ModelTierList } from "#neoastra";

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
}>;

export const maximumTierTargets = 16;
const maximumEfforts = 8;
const text = (value: unknown, limit = 256): value is string => typeof value === "string" && value.trim().length > 0 && value.length <= limit;

export const emptyTiers: TierState = Object.freeze({
  tiers: Object.freeze({ complex: [], standard: [], quick: [] }),
  choices: Object.freeze({ complex: null, standard: null, quick: null }),
  fallback: Object.freeze({ enabled: false, onQuota: true, onUnavailable: true }),
  source: "global",
});

/** Whether the user set no tier: the composer then offers none and new sessions keep the default provider. */
export const noTiers = (state: TierState | null | undefined) => !state || tierNames.every(tier => state.tiers[tier].length === 0);

/** The tiers a reply of the host gives; null for one that is not such a reading. A tier the host did not list is empty. */
export function readTierState(reply: Pick<GlobalConfigModelTiersResponse, "tiers" | "fallback"> & Partial<Pick<GlobalConfigModelTiersResponse, "source">> | null | undefined): TierState | null {
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
  return { tiers, choices, fallback: { enabled: reply.fallback.enabled === true, onQuota: reply.fallback.onQuota !== false, onUnavailable: reply.fallback.onUnavailable !== false },
    source: reply.source === "space" ? "space" : "global" };
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

/**
 * The tiers of the window, read from the host for each scope: Settings, the page of a space and the composers show
 * the same readings, and a save tells every one of them, after reading again the scopes it may change.
 */
export function createTierStore(read: Read) {
  let readings: ReadonlyMap<string, TierState> = new Map();
  const scopes = new Map<string, Readonly<{ epoch: string; scope: TierScope | null }>>();
  const loading = new Set<string>();
  const listeners = new Set<() => void>();
  const tell = () => { for (const listener of [...listeners]) listener(); };
  const put = (key: string, state: TierState) => { readings = new Map(readings).set(key, state); tell(); };
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
    } catch { /* The reading the window has stays. */ }
    finally { loading.delete(key); }
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