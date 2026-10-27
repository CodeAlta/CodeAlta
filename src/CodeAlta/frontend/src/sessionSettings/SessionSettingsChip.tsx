import { useEffect, useState } from "react";
import { HTMLSelect } from "@blueprintjs/core";
import type { SandboxSessionResponse, SessionPermissionModeChoice, SessionPromptChoice } from "#neoastra";
import { ComposerModelPicker } from "../ComposerModelPicker";
import { ComposerSelectionFields, ReasoningSlider } from "../ComposerSelection";
import { PermissionModeSelect } from "../PermissionModeSelect";
import { ReviewBeforeCommitSelect } from "../ReviewBeforeCommitSelect";
import { permissionModeName } from "../permissionModes";
import { reasoningShown } from "../promptSurface/promptSurface";
import { SessionSandboxSelect } from "../sandbox/SessionSandboxSelect";
import { notSandboxed, sessionSandboxSummary } from "../sandbox/sandboxChoices";
import { useShellLanguage } from "../shellLanguage";
import type { RunProvider, RunsWith } from "../workItems/runsWith";
import { chooseEffort, chooseModel, chooseProvider, shownModel, switchDefaults, usesDefaults, type SessionSettings } from "./sessionSettings";
import type { SessionSettingsApi, SessionSettingsModel } from "./sessionSettingsApi";
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
  const models = useKeyedRead<readonly SessionSettingsModel[]>(scope && providerId ? `${scope}\n${providerId}` : null,
    api && providerId ? signal => api.models(providerId, signal) : null, []);
  const shown = shownModel(providers, models.value ?? null, recorded, settings);
  const prompts = useKeyedRead<Readonly<{ prompts: readonly SessionPromptChoice[]; defaultPromptId: string | null }> | null>(scope, api ? signal => api.prompts(signal) : null, null);
  const permissions = useKeyedRead<Readonly<{ modes: readonly SessionPermissionModeChoice[]; defaultMode: string | null }> | null>(scope && providerId ? `${scope}\n${providerId}` : null,
    api && providerId ? signal => api.permissionModes(providerId, signal) : null, null);
  const review = useKeyedRead<boolean | null>(scope, api ? signal => api.reviewSetting(signal) : null, null);
  const sandbox = useKeyedRead<SandboxSessionResponse | null>(scope ? `${scope}\n${shown.providerId}\n${shown.modelId}` : null,
    api ? signal => api.sandbox(shown.providerId, shown.modelId, signal) : null, null);
  return {
    shown, provider: shown.provider, models: models.value ?? [], loadingModels: models.loading,
    prompts: prompts.value?.prompts ?? [], defaultPromptId: prompts.value?.defaultPromptId ?? null, loadingPrompts: prompts.loading,
    permissionModes: permissions.value?.modes ?? [], defaultPermissionMode: permissions.value?.defaultMode ?? null,
    reviewSetting: review.value ?? null, sandbox: sandbox.value ?? null,
  };
}

export type SessionSettingsChoices = ReturnType<typeof useSessionSettingsChoices>;

/**
 * The Agent chip of a session that an automation, a task or an issue starts: the chip and the form of the composer,
 * with the switch of the defaults at the top. With the defaults, the rows are disabled and show what the session will
 * run with, so that the user sees which provider and model the settings give.
 */
export function SessionSettingsChip({ id, providers, choices, value, onChange, disabled = false }: {
  /** Tells the controls of this form apart from the others of the page. */
  id: string;
  /** The enabled providers. */
  providers: readonly RunProvider[];
  choices: SessionSettingsChoices;
  value: SessionSettings; onChange: (value: SessionSettings) => void;
  disabled?: boolean;
}) {
  const { t } = useShellLanguage();
  const { shown, provider, models } = choices;
  const defaults = usesDefaults(value);
  const locked = disabled || defaults;
  const model = models.find(candidate => candidate.id === shown.modelId);
  const modelName = (candidate: SessionSettingsModel | undefined) => candidate ? candidate.name || candidate.id : undefined;
  const reasoning = reasoningShown(shown.reasoningEffort, model && { name: modelName(model)!, efforts: model.efforts, effortsComplete: model.effortsComplete });
  const agentId = value.agentPromptId ?? choices.defaultPromptId;
  const agentName = choices.prompts.find(prompt => prompt.id === agentId)?.name ?? agentId ?? t(choices.loadingPrompts ? "Loading…" : "Default");
  // Without a default provider in the settings, the first enabled one is used: the list says so.
  const anyDefault = providers.some(candidate => candidate.isDefault);
  const providerLabel = (candidate: RunProvider) => candidate.isDefault ? t("{name} (default)", { name: candidate.name || candidate.id })
    : !anyDefault && candidate === providers[0] ? t("{name} (first enabled)", { name: candidate.name || candidate.id }) : candidate.name || candidate.id;
  const permission = choices.permissionModes.length > 0 ? value.permissionMode ?? choices.defaultPermissionMode : undefined;
  const permissionName = (mode: string | null) => mode === null ? t("The setting of the CLI") : permissionModeName(mode) ? t(permissionModeName(mode)!) : mode;
  return <ComposerSelectionFields sessionId={id} disabled={disabled}
    defaults={{ on: defaults, disabled: !shown.providerId, onChange: on => onChange(switchDefaults(value, on, shown)) }}
    summary={{ agent: agentName, provider: provider ? provider.name || provider.id : shown.providerId ?? t("No provider"), providerKey: shown.providerId, modelId: shown.modelId,
      model: modelName(model) ?? shown.modelId ?? t(choices.loadingModels ? "Loading…" : "No model"),
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
    provider={<HTMLSelect fill id={`composer-provider-${id}`} aria-label={t("Provider")} value={shown.providerId ?? ""} disabled={locked}
      onChange={event => onChange(chooseProvider(value, event.target.value))}>
      {!shown.providerId && <option value="">{t("No provider")}</option>}
      {shown.providerId && !provider && <option value={shown.providerId}>{`${shown.providerId} · ${t("Unverified")}`}</option>}
      {providers.map(candidate => <option key={candidate.id} value={candidate.id}>{providerLabel(candidate)}</option>)}
    </HTMLSelect>}
    model={<ComposerModelPicker id={`composer-model-${id}`} value={shown.modelId} models={models.map(candidate => ({ ...candidate, name: modelName(candidate)! }))}
      providerKey={shown.providerId} disabled={locked || !provider} loading={choices.loadingModels} onChange={modelId => onChange(chooseModel(value, modelId, shown))} />}
    reasoning={<ReasoningSlider value={shown.reasoningEffort} efforts={model ? model.efforts : null} complete={!!model?.effortsComplete} model={modelName(model)} disabled={locked}
      onChange={effort => onChange(chooseEffort(value, effort, shown))} />}
    permission={choices.permissionModes.length > 0 ? <PermissionModeSelect id={`composer-permission-${id}`} value={value.permissionMode} modes={choices.permissionModes}
      defaultMode={choices.defaultPermissionMode} disabled={locked} onChange={mode => onChange({ ...value, permissionMode: mode || null })} /> : undefined}
    review={choices.reviewSetting !== null && <ReviewBeforeCommitSelect id={`composer-review-${id}`} own={value.reviewBeforeCommit} setting={choices.reviewSetting} disabled={locked}
      onChange={reviewBeforeCommit => onChange({ ...value, reviewBeforeCommit })} />}
    sandbox={choices.sandbox && <SessionSandboxSelect id={`composer-sandbox-${id}`} own={value.sandbox} inherited={choices.sandbox.profile} choices={choices.sandbox.choices}
      disabled={locked} onChange={sandbox => onChange({ ...value, sandbox })} />}
    options={{ changed: changedSessionOptions(value.options),
      content: <SessionOptionsFields id={id} value={value.options} disabled={disabled} onChange={options => onChange({ ...value, options })} /> }} />;
}
