// What a session started elsewhere than in a composer (an automation, a task, an issue) runs with, as the form of its
// Agent chip chooses it. A part that is null is left to the defaults, which the form shows.
import type { SessionOptionsChoice } from "#neoastra";
import { runModel, runProvider, type RunModel, type RunProvider, type RunsWith } from "../workItems/runsWith";

/**
 * The choices of the form; null leaves a part to the defaults. The options (sub-sessions) are not models of the
 * defaults: like the agent prompt, they keep their choice whether the defaults are used or not.
 */
export type SessionSettings = Readonly<{
  agentPromptId: string | null;
  providerId: string | null; modelId: string | null; reasoningEffort: string | null;
  permissionMode: string | null; reviewBeforeCommit: boolean | null; sandbox: string | null;
  options: SessionOptionsChoice | null;
  /**
   * The tier of models of the session (`complex`, `standard`, `quick`), or Adaptive (`adaptive`, `adaptive:<level>`),
   * as the composer names them: it gives the model, the tiers of the space saying which now. Null for a model chosen
   * by hand, or the defaults. Only where the form offers tiers.
   */
  tier: string | null;
}>;

/** The provider, the model and the effort the form shows: what is chosen, else what the defaults give. */
export type ShownModel = Readonly<{ providerId: string | null; modelId: string | null; reasoningEffort: string | null }>;

/** What a start asks of the host; null parts leave it to the host, which takes the defaults. A tier gives the model, which the request then leaves to the host. */
export type SessionStartRequest = SessionSettings;

/**
 * The provider, the model and the effort the form shows. A provider, a model or an effort the user chose is shown as
 * it is, even when it is no longer offered; the others are what the defaults give: what the work was proposed with
 * (`recorded`), then the default provider, its model and its effort, as a new session takes them.
 */
export function shownModel(providers: readonly RunProvider[], models: readonly RunModel[] | null, recorded: RunsWith | null | undefined, settings: SessionSettings)
  : ShownModel & Readonly<{ provider: RunProvider | null }> {
  const provider = settings.providerId ? providers.find(candidate => candidate.id.toLowerCase() === settings.providerId!.toLowerCase()) ?? null : runProvider(providers, recorded, {});
  const providerId = settings.providerId ?? provider?.id ?? null;
  // Unknown until the models are read, or not offered: what was chosen.
  if (!provider || !models || settings.modelId && !models.some(model => model.id === settings.modelId))
    return { provider, providerId, modelId: settings.modelId, reasoningEffort: settings.reasoningEffort };
  const resolved = runModel(provider, models, recorded, { modelId: settings.modelId ?? undefined, reasoningEffort: settings.reasoningEffort ?? undefined });
  return { provider, providerId, modelId: resolved.modelId, reasoningEffort: settings.reasoningEffort ?? resolved.reasoningEffort };
}

/** Every part left to the defaults. */
export const defaultSessionSettings: SessionSettings = Object.freeze({
  agentPromptId: null, providerId: null, modelId: null, reasoningEffort: null, permissionMode: null, reviewBeforeCommit: null, sandbox: null, options: null, tier: null,
});

/** Whether the provider, the model, the effort, the tier, the permission mode, the review and the sandbox are the defaults. The agent prompt has its own default. */
export function usesDefaults(settings: SessionSettings): boolean {
  return settings.providerId === null && settings.modelId === null && settings.reasoningEffort === null && settings.tier === null
    && settings.permissionMode === null && settings.reviewBeforeCommit === null && settings.sandbox === null;
}

/**
 * The switch of the defaults: on, every part but the agent prompt and the options goes back to the defaults; off, what
 * the defaults give now becomes the choice of the user, who changes it from there, with the tier they give
 * (`inheritedTier`: the one of the session the new one takes its model from).
 */
export function switchDefaults(settings: SessionSettings, on: boolean, shown: ShownModel, inheritedTier: string | null = null): SessionSettings {
  if (on) return { ...defaultSessionSettings, agentPromptId: settings.agentPromptId, options: settings.options };
  return { ...settings, tier: settings.tier ?? inheritedTier, providerId: settings.providerId ?? shown.providerId, modelId: settings.modelId ?? shown.modelId,
    reasoningEffort: settings.reasoningEffort ?? (settings.modelId ?? shown.modelId ? shown.reasoningEffort : null) };
}

// A provider, a model or an effort chosen by hand leaves the tier that gave them.

/** Another provider: its model, its effort and its permission mode are its defaults until chosen. */
export function chooseProvider(settings: SessionSettings, providerId: string): SessionSettings {
  return { ...settings, providerId, modelId: null, reasoningEffort: null, permissionMode: null, tier: null };
}

/** Another model of the provider shown: its effort is its default until chosen. */
export function chooseModel(settings: SessionSettings, modelId: string, shown: ShownModel): SessionSettings {
  return { ...settings, providerId: settings.providerId ?? shown.providerId, modelId, reasoningEffort: null, tier: null };
}

/** Another effort of the model shown, which becomes a choice with it. */
export function chooseEffort(settings: SessionSettings, effort: string, shown: ShownModel): SessionSettings {
  return { ...settings, providerId: settings.providerId ?? shown.providerId, modelId: settings.modelId ?? shown.modelId, reasoningEffort: effort, tier: null };
}

/**
 * A tier of models, or Adaptive, as the choice of the form: the model the tiers give for it now (`choice`), with its
 * effort. The permission mode stays when the provider does: the one chosen, else the one the form shows (`shownProviderId`),
 * as for a tier saved without the model it gave.
 */
export function chooseTier(settings: SessionSettings, tier: string, choice: Readonly<{ providerKey: string; modelId: string; reasoning: string | null }>,
  shownProviderId: string | null = null): SessionSettings {
  const same = (settings.providerId ?? shownProviderId)?.toLowerCase() === choice.providerKey.toLowerCase();
  return { ...settings, providerId: choice.providerKey, modelId: choice.modelId, reasoningEffort: choice.reasoning, permissionMode: same ? settings.permissionMode : null, tier };
}

/**
 * What a start asks of the host: nothing of the model with the defaults, which the host resolves as the form shows
 * them; the tier alone when one is chosen, which gives the model on the host; otherwise the provider, the model and
 * the effort the form shows.
 */
export function startRequest(settings: SessionSettings, shown: ShownModel): SessionStartRequest {
  if (settings.tier) return { ...settings, providerId: null, modelId: null, reasoningEffort: null };
  if (usesDefaults(settings)) return { ...defaultSessionSettings, agentPromptId: settings.agentPromptId, options: settings.options };
  const providerId = settings.providerId ?? shown.providerId;
  const modelId = providerId ? settings.modelId ?? shown.modelId : null;
  return { ...settings, providerId, modelId, reasoningEffort: modelId ? settings.reasoningEffort ?? shown.reasoningEffort : null };
}
