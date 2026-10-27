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
}>;

/** The provider, the model and the effort the form shows: what is chosen, else what the defaults give. */
export type ShownModel = Readonly<{ providerId: string | null; modelId: string | null; reasoningEffort: string | null }>;

/** What a start asks of the host; null parts leave it to the host, which takes the defaults. */
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
  agentPromptId: null, providerId: null, modelId: null, reasoningEffort: null, permissionMode: null, reviewBeforeCommit: null, sandbox: null, options: null,
});

/** Whether the provider, the model, the effort, the permission mode, the review and the sandbox are the defaults. The agent prompt has its own default. */
export function usesDefaults(settings: SessionSettings): boolean {
  return settings.providerId === null && settings.modelId === null && settings.reasoningEffort === null
    && settings.permissionMode === null && settings.reviewBeforeCommit === null && settings.sandbox === null;
}

/**
 * The switch of the defaults: on, every part but the agent prompt and the options goes back to the defaults; off, what
 * the defaults give now becomes the choice of the user, who changes it from there.
 */
export function switchDefaults(settings: SessionSettings, on: boolean, shown: ShownModel): SessionSettings {
  if (on) return { ...defaultSessionSettings, agentPromptId: settings.agentPromptId, options: settings.options };
  return { ...settings, providerId: settings.providerId ?? shown.providerId, modelId: settings.modelId ?? shown.modelId,
    reasoningEffort: settings.reasoningEffort ?? (settings.modelId ?? shown.modelId ? shown.reasoningEffort : null) };
}

/** Another provider: its model, its effort and its permission mode are its defaults until chosen. */
export function chooseProvider(settings: SessionSettings, providerId: string): SessionSettings {
  return { ...settings, providerId, modelId: null, reasoningEffort: null, permissionMode: null };
}

/** Another model of the provider shown: its effort is its default until chosen. */
export function chooseModel(settings: SessionSettings, modelId: string, shown: ShownModel): SessionSettings {
  return { ...settings, providerId: settings.providerId ?? shown.providerId, modelId, reasoningEffort: null };
}

/** Another effort of the model shown, which becomes a choice with it. */
export function chooseEffort(settings: SessionSettings, effort: string, shown: ShownModel): SessionSettings {
  return { ...settings, providerId: settings.providerId ?? shown.providerId, modelId: settings.modelId ?? shown.modelId, reasoningEffort: effort };
}

/**
 * What a start asks of the host: nothing of the model with the defaults, which the host resolves as the form shows
 * them; otherwise the provider, the model and the effort the form shows.
 */
export function startRequest(settings: SessionSettings, shown: ShownModel): SessionStartRequest {
  if (usesDefaults(settings)) return { ...defaultSessionSettings, agentPromptId: settings.agentPromptId, options: settings.options };
  const providerId = settings.providerId ?? shown.providerId;
  const modelId = providerId ? settings.modelId ?? shown.modelId : null;
  return { ...settings, providerId, modelId, reasoningEffort: modelId ? settings.reasoningEffort ?? shown.reasoningEffort : null };
}
