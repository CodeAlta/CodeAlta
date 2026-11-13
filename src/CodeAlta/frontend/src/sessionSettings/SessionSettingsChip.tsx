import { useEffect, useState } from "react";
import { HTMLSelect } from "@blueprintjs/core";
import type { SandboxSessionResponse, SessionPermissionModeChoice, SessionPromptChoice } from "#neoastra";
import { AdaptiveSetupDialog } from "../AdaptiveSetup";
import { ComposerModelPicker } from "../ComposerModelPicker";
import { ComposerProviderPicker } from "../ComposerProviderPicker";
import { ComposerSelectionFields, ReasoningSlider } from "../ComposerSelection";
import { PermissionModeSelect } from "../PermissionModeSelect";
import { ReviewBeforeCommitSelect } from "../ReviewBeforeCommitSelect";
import { permissionModeName } from "../permissionModes";
import { adaptiveChoice, adaptiveLevelLabels, adaptiveLevelOf, adaptiveTier, adaptiveTierOf, composerAdaptiveEntry, composerTierEntries, readAdaptiveTier, tierLabels, tierNames,
  useTiers, type TierState } from "../modelTiers";
import { reasoningShown } from "../promptSurface/promptSurface";
import { SessionSandboxSelect } from "../sandbox/SessionSandboxSelect";
import { notSandboxed, sessionSandboxSummary } from "../sandbox/sandboxChoices";
import { useShellLanguage } from "../shellLanguage";
import type { RunProvider, RunsWith } from "../workItems/runsWith";
import { chooseEffort, chooseModel, chooseProvider, shownModel, switchDefaults, usesDefaults, type SessionSettings } from "./sessionSettings";
import type { SessionSettingsApi, SessionSettingsModel, SessionSettingsModels } from "./sessionSettingsApi";
import { SessionOptionsFields } from "./SessionOptionsFields";
import { changedSessionOptions, sessionOptionsOf } from "./sessionOptions";

// The answer of one read, for the key it was asked with: an answer for another key is not shown.
type Keyed<T> = Readonly<{ key: string; value: T }>;

function useKeyedRead<T>(key: string | null, read: ((signal: AbortSignal) => Promise<T>) | null, failed: T): Readonly<{ value: T | undefined; loading: boolean }> {
  const [state, setState] = useState<Keyed<T> | null>(null);
  useEffect(() => {
    if (key === null || !read) return;
    const abort = new AbortController();
    void read(abort.signal).then(value => { if (!abort.signal.aborted) setState({ key, value }); },
      () => { if (!abort.signal.aborted) setState({ key, value: failed }); });
    return () => abort.abort();
    // The key names everything the read depends on.
  }, [key]);
  const current = key !== null && state?.key === key;
  return { value: current ? state.value : undefined, loading: key !== null && !!read && !current };
}

/**
 * What the form of the Agent chip of a session started by an automation, a task or an issue shows: the catalogs of
 * the host for the provider and the model shown, and what the defaults give.
 */
export function useSessionSettingsChoices(api: SessionSettingsApi | null, providers: readonly RunProvider[], recorded: RunsWith | null | undefined, settings: SessionSettings) {
  // Catalogs of another host, project or space are read again.
  const scope = api?.key ?? null;
  const resolved = shownModel(providers, null, recorded, settings);
  const providerId = resolved.provider?.id ?? null;
  const models = useKeyedRead<SessionSettingsModels>(scope && providerId ? `${scope}\n${providerId}` : null,
    api && providerId ? signal => api.models(providerId, signal) : null, { models: [], order: null });
  const shown = shownModel(providers, models.value?.models ?? null, recorded, settings);
  const prompts = useKeyedRead<Readonly<{ prompts: readonly SessionPromptChoice[]; defaultPromptId: string | null }> | null>(scope, api ? signal => api.prompts(signal) : null, null);
  const permissions = useKeyedRead<Readonly<{ modes: readonly SessionPermissionModeChoice[]; defaultMode: string | null }> | null>(scope && providerId ? `${scope}\n${providerId}` : null,
    api && providerId ? signal => api.permissionModes(providerId, signal) : null, null);
  const review = useKeyedRead<boolean | null>(scope, api ? signal => api.reviewSetting(signal) : null, null);
  const tiers = useTiers(api?.tiers?.epoch ?? null, api?.tiers?.spaceId ? { spaceId: api.tiers.spaceId } : null);
  const sandbox = useKeyedRead<SandboxSessionResponse | null>(scope ? `${scope}\n${shown.providerId}\n${shown.modelId}` : null,
    api ? signal => api.sandbox(shown.providerId, shown.modelId, signal) : null, null);
  return {
    shown, provider: shown.provider, models: models.value?.models ?? [], modelsOrder: models.value?.order ?? null, loadingModels: models.loading,
    prompts: prompts.value?.prompts ?? [], defaultPromptId: prompts.value?.defaultPromptId ?? null, loadingPrompts: prompts.loading,
    permissionModes: permissions.value?.modes ?? [], defaultPermissionMode: permissions.value?.defaultMode ?? null,
    reviewSetting: review.value ?? null, sandbox: sandbox.value ?? null,
    /** The tiers of models of the space, with the model each gives now; null until read, or where the form offers none. */
    tiers: api?.tiers ? tiers : null,
  };
}

export type SessionSettingsChoices = ReturnType<typeof useSessionSettingsChoices>;

/**
 * The tier of models, or Adaptive, of a start whose host takes one: `complex`, `standard`, `quick`, `adaptive` or
 * `adaptive:<level>`, as the composer names them; null for a model chosen by hand.
 */
export type SessionSettingsTier = Readonly<{
  value: string | null;
  /** The tier the defaults give, shown while they are used: the one of the session the new one takes its model from. */
  inherited?: string | null;
  onChange: (tier: string | null) => void;
}>;

/** The name the chip gives a tier, or Adaptive at a level; null for none. */
export function sessionTierLabel(tier: string | null | undefined, t: (key: "Complex" | "Standard" | "Quick" | "Adaptive" | "Low" | "Medium" | "High" | "Max") => string): string | null {
  if (!tier) return null;
  const adaptive = readAdaptiveTier(tier);
  if (adaptive.adaptive) return adaptive.level ? `${t("Adaptive")} ${t(adaptiveLevelLabels[adaptive.level])}` : t("Adaptive");
  const name = tierNames.find(candidate => candidate === tier);
  return name ? t(tierLabels[name]) : null;
}

/** The choices of a tier: its model, with its effort, from what the tiers give now; null when it gives none. */
function tierSettings(value: SessionSettings, tiers: TierState | null, tier: string): SessionSettings | null {
  const adaptive = readAdaptiveTier(tier);
  const choice = adaptive.adaptive ? adaptiveChoice(tiers, adaptive.level) : tiers?.choices[tierNames.find(name => name === tier) ?? "standard"] ?? null;
  if (!choice) return null;
  const same = value.providerId?.toLowerCase() === choice.providerKey.toLowerCase();
  return { ...value, providerId: choice.providerKey, modelId: choice.modelId, reasoningEffort: choice.reasoning, permissionMode: same ? value.permissionMode : null };
}

/**
 * The Agent chip of a session that an automation, a task or an issue starts: the chip and the form of the composer,
 * with the switch of the defaults at the top. With the defaults, the rows are disabled and show what the session will
 * run with, so that the user sees which provider and model the settings give.
 */
export function SessionSettingsChip({ id, providers, choices, value, onChange, disabled = false, withDefaults = true, privateOption = true, tier = null }: {
  /** Tells the controls of this form apart from the others of the page. */
  id: string;
  /** The enabled providers. */
  providers: readonly RunProvider[];
  choices: SessionSettingsChoices;
  value: SessionSettings; onChange: (value: SessionSettings) => void;
  disabled?: boolean;
  /**
   * Whether the form has the switch of the defaults. Without it, as the composer of a new session, every row can be
   * changed at once, and a part not changed is the default it shows.
   */
  withDefaults?: boolean;
  /** Whether the options offer to make the session Private (see `SessionOptionsFields`). */
  privateOption?: boolean;
  /** The tier of models of the start, which the model list then offers with Adaptive, as the composer does; none where the start takes none. */
  tier?: SessionSettingsTier | null;
}) {
  const { t } = useShellLanguage();
  const [adaptiveSetup, setAdaptiveSetup] = useState(false);
  const { shown, provider, models } = choices;
  const defaults = usesDefaults(value);
  const locked = disabled || withDefaults && defaults;
  // A choice by hand of what a tier gives leaves the tier.
  const change = (next: SessionSettings) => { onChange(next); tier?.onChange(null); };
  const chooseTier = (name: string) => {
    const next = tierSettings(value, choices.tiers, name);
    if (!next || !tier) return;
    onChange(next);
    tier.onChange(name);
  };
  const shownTier = tier ? tier.value ?? (withDefaults && defaults ? tier.inherited ?? null : null) : null;
  const shownAdaptive = readAdaptiveTier(shownTier);
  const tierLabel = sessionTierLabel(shownTier, t);
  const model = models.find(candidate => candidate.id === shown.modelId);
  const modelName = (candidate: SessionSettingsModel | undefined) => candidate ? candidate.name || candidate.id : undefined;
  const reasoning = reasoningShown(shown.reasoningEffort, model && { name: modelName(model)!, efforts: model.efforts, effortsComplete: model.effortsComplete });
  const agentId = value.agentPromptId ?? choices.defaultPromptId;
  const agentName = choices.prompts.find(prompt => prompt.id === agentId)?.name ?? agentId ?? t(choices.loadingPrompts ? "Loading…" : "Default");
  // Without a default provider in the settings, the first enabled one is used: the list says so.
  const anyDefault = providers.some(candidate => candidate.isDefault);
  const permission = choices.permissionModes.length > 0 ? value.permissionMode ?? choices.defaultPermissionMode : undefined;
  const permissionName = (mode: string | null) => mode === null ? t("The setting of the CLI") : permissionModeName(mode) ? t(permissionModeName(mode)!) : mode;
  return <ComposerSelectionFields sessionId={id} disabled={disabled}
    // Off, what the defaults give becomes the choice, with the tier they give.
    defaults={withDefaults ? { on: defaults, disabled: !shown.providerId, onChange: on => {
      onChange(switchDefaults(value, on, shown));
      tier?.onChange(on ? null : tier.inherited ?? null);
    } } : null}
    summary={{ agent: agentName, provider: provider ? provider.name || provider.id : shown.providerId ?? t("No provider"), providerKey: shown.providerId, modelId: shown.modelId,
      model: (tierLabel ? `${tierLabel} · ` : "") + (modelName(model) ?? shown.modelId ?? t(choices.loadingModels ? "Loading…" : "No model")),
      reasoning: reasoning.effort ?? (choices.loadingModels ? t("Loading…") : null),
      reasoningNote: reasoning.note === "none" ? t("{model} has no reasoning effort", { model: modelName(model)! })
        : reasoning.note === "unused" ? `${t("Reasoning")}: ${shown.reasoningEffort} · ${t("Not used")}` : null,
      permission: permission === undefined ? null : { mode: permission, name: permissionName(permission) },
      review: choices.reviewSetting === null ? null : value.reviewBeforeCommit ?? choices.reviewSetting,
      sandbox: !choices.sandbox ? null : value.sandbox === null ? sessionSandboxSummary(choices.sandbox)
        : { profile: value.sandbox.toLowerCase() === notSandboxed ? null : value.sandbox, source: "session", problem: null },
      subSessions: sessionOptionsOf(value.options).subSessions }}
    agent={<HTMLSelect fill id={`composer-agent-${id}`} aria-label={t("Agent prompt")} value={agentId ?? ""} disabled={disabled || choices.prompts.length === 0}
      onChange={event => onChange({ ...value, agentPromptId: event.target.value === choices.defaultPromptId ? null : event.target.value || null })}>
      {!agentId && <option value="">{t(choices.loadingPrompts ? "Loading…" : "Default")}</option>}
      {agentId && !choices.prompts.some(prompt => prompt.id === agentId) && <option value={agentId}>{agentId}</option>}
      {choices.prompts.map(prompt => <option key={prompt.id} value={prompt.id}>
        {prompt.id === choices.defaultPromptId && prompt.name !== t("Default") ? `${prompt.name} · ${t("Default")}` : prompt.name}</option>)}
    </HTMLSelect>}
    provider={<ComposerProviderPicker id={`composer-provider-${id}`} value={shown.providerId} disabled={locked}
      providers={providers.map(candidate => ({ id: candidate.id, name: candidate.name, isDefault: candidate.isDefault, firstEnabled: !anyDefault && candidate === providers[0] }))}
      onChange={providerId => change(chooseProvider(value, providerId))} />}
    model={<ComposerModelPicker id={`composer-model-${id}`} value={shown.modelId} models={models.map(candidate => ({ ...candidate, name: modelName(candidate)! }))}
      order={choices.modelsOrder} providerKey={shown.providerId} disabled={locked || !provider} loading={choices.loadingModels} onChange={modelId => change(chooseModel(value, modelId, shown))}
      {...tier ? {
        tiers: composerTierEntries(choices.tiers, t), currentTier: shownAdaptive.adaptive ? adaptiveTier : shownTier, onChooseTier: chooseTier,
        adaptive: composerAdaptiveEntry(choices.tiers, t),
        currentAdaptiveLevel: shownAdaptive.adaptive ? adaptiveLevelOf(choices.tiers?.adaptive, shownAdaptive.level)?.level ?? null : null,
        // Adaptive that is not set up says so, and leads to its settings, as in the composer.
        onChooseAdaptive: (level?: string) => { if (!choices.tiers?.adaptive || !choices.tiers.adaptiveReady) setAdaptiveSetup(true); else chooseTier(adaptiveTierOf(level)); },
      } : {}} />}
    reasoning={<ReasoningSlider value={shown.reasoningEffort} efforts={model ? model.efforts : null} complete={!!model?.effortsComplete} model={modelName(model)} disabled={locked}
      onChange={effort => change(chooseEffort(value, effort, shown))} />}
    permission={choices.permissionModes.length > 0 ? <PermissionModeSelect id={`composer-permission-${id}`} value={value.permissionMode} modes={choices.permissionModes}
      defaultMode={choices.defaultPermissionMode} disabled={locked} onChange={mode => onChange({ ...value, permissionMode: mode || null })} /> : undefined}
    review={choices.reviewSetting !== null && <ReviewBeforeCommitSelect id={`composer-review-${id}`} own={value.reviewBeforeCommit} setting={choices.reviewSetting} disabled={locked}
      onChange={reviewBeforeCommit => onChange({ ...value, reviewBeforeCommit })} />}
    sandbox={choices.sandbox && <SessionSandboxSelect id={`composer-sandbox-${id}`} own={value.sandbox} inherited={choices.sandbox.profile} choices={choices.sandbox.choices}
      disabled={locked} onChange={sandbox => onChange({ ...value, sandbox })} />}
    options={{ changed: changedSessionOptions(value.options),
      content: <><SessionOptionsFields id={id} value={value.options} disabled={disabled} privateOption={privateOption} onChange={options => onChange({ ...value, options })} />
        {tier && <AdaptiveSetupDialog isOpen={adaptiveSetup} onClose={() => setAdaptiveSetup(false)} />}</> }} />;
}
