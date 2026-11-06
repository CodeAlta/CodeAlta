import { useState } from "react";
import { Button, HTMLSelect, Switch } from "@blueprintjs/core";
import { globalConfig, type AdaptiveLevelSetting, type ModelCatalogModel, type ModelCatalogProvider } from "#neoastra";
import { AppIcon } from "./AppIcon";
import { adaptiveLevelLabels, adaptiveLevelNames, adaptiveLevelSummary, maximumAdaptiveCritics, readTierState, startingEfforts,
  type AdaptiveChoice, type AdaptiveLevel, type AdaptiveLevelName, type TierState, type TierStore, type TierTarget } from "./modelTiers";
import { TierModelPicker } from "./ModelTierSettings";
import { ProviderIcon } from "./ProviderIcon";
import { useShellLanguage } from "./shellLanguage";

type SaveAdaptive = Pick<typeof globalConfig, "saveAdaptive">;

const noLevels: AdaptiveChoice = Object.freeze({ level: "medium", levels: [] });

// A level as the host saves it.
const toSetting = (level: AdaptiveLevel): AdaptiveLevelSetting => ({
  level: level.level, model: { ...level.model, reasoning: [...level.model.reasoning] },
  critics: level.critics.map(critic => ({ ...critic, reasoning: [...critic.reasoning] })), choice: null,
});

/**
 * Adaptive on Settings > Models, under the tiers: whether it is set up, the level a session takes when none is chosen,
 * and for each level the model of its turns and the critics that review its changes of code, of any provider. It offers
 * the combinations tested for the providers that are ready, and says how to choose the models without them. Saved at once.
 */
export function AdaptiveSettings({ epoch, state, api, store, onSaved, providers, models }: {
  epoch: string; state: TierState; api: SaveAdaptive; store: TierStore; onSaved: (state: TierState) => void;
  providers: readonly ModelCatalogProvider[]; models: ReadonlyMap<string, readonly ModelCatalogModel[]>;
}) {
  const { t } = useShellLanguage();
  const [shown, setShown] = useState<AdaptiveChoice | null | undefined>(undefined);
  const [notice, setNotice] = useState<string | null>(null);
  const adaptive = shown === undefined ? state.adaptive : shown;
  const proposal = state.adaptiveProposal;

  async function save(next: AdaptiveChoice | null) {
    setShown(next); setNotice(null);
    try {
      const reply = await api.saveAdaptive({ expectedEpoch: epoch, adaptive: next ? { level: next.level, levels: next.levels.map(toSetting) } : null },
        { timeoutMilliseconds: 30_000 });
      const saved = reply.status === "ok" ? readTierState(reply) : null;
      if (saved) { store.set(epoch, saved); onSaved(saved); setShown(undefined); return; }
      setShown(undefined);
      setNotice(reply.message ?? t("Adaptive was not saved ({status}).", { status: reply.status }));
    } catch {
      setShown(undefined);
      setNotice(t("Adaptive was not saved ({status}).", { status: "no_answer" }));
    }
  }

  const providerOf = (key: string) => providers.find(provider => provider.id.toLowerCase() === key.toLowerCase());
  const modelOf = (target: TierTarget) => models.get(providerOf(target.providerKey)?.id ?? target.providerKey)?.find(model => model.id.toLowerCase() === target.modelId.toLowerCase());
  // A level changed, added, or taken out (null).
  const withLevel = (current: AdaptiveChoice, level: AdaptiveLevelName, change: (existing: AdaptiveLevel | undefined) => AdaptiveLevel | null | undefined): AdaptiveChoice => {
    const changed = change(current.levels.find(existing => existing.level === level));
    const others = current.levels.filter(existing => existing.level !== level);
    return { ...current, levels: (changed ? [...others, changed] : others).sort((left, right) => adaptiveLevelNames.indexOf(left.level) - adaptiveLevelNames.indexOf(right.level)) };
  };
  const targetOf = (provider: ModelCatalogProvider, model: ModelCatalogModel): TierTarget =>
    ({ providerKey: provider.id, modelId: model.id, reasoning: startingEfforts(model).slice(0, 1) });

  // A model of a level: its name and provider, the button that changes it, and its reasoning effort.
  const modelField = (target: TierTarget, label: string, change: (next: TierTarget) => void, remove?: () => void) => {
    const provider = providerOf(target.providerKey);
    const model = modelOf(target);
    const name = model?.name ?? target.modelId;
    const efforts = model?.efforts ?? [];
    return <span className="adaptive-model" key={`${target.providerKey}:${target.modelId}`}>
      <ProviderIcon providerKey={provider?.id ?? target.providerKey} known={provider ? { type: provider.type, name: provider.name } : undefined} size={14} />
      <TierModelPicker providers={providers} models={models} offered={() => true} note={() => null} taken={[]} current={target} className="model-tier-model-target"
        onChoose={(chosen, choice) => change(targetOf(chosen, choice))}>
        <button type="button" className="model-tier-model" aria-label={t("Change the model of {name}", { name: `${label} · ${name}` })}
          title={`${target.providerKey}:${target.modelId}`}>
          <span className="model-tier-model-text">
            <strong>{name}</strong>
            <span className="model-tier-provider" data-missing={provider ? undefined : "true"} title={provider ? undefined : t("Not a configured provider")}>{provider?.name ?? target.providerKey}</span>
          </span>
          <AppIcon name="chevronDown" size={12} className="model-tier-model-chevron" />
        </button>
      </TierModelPicker>
      {efforts.length > 0 && <HTMLSelect minimal className="adaptive-effort" aria-label={t("Reasoning effort of {name}", { name: `${label} · ${name}` })}
        value={target.reasoning[0] ?? ""} onChange={event => change({ ...target, reasoning: event.currentTarget.value ? [event.currentTarget.value] : [] })}>
        {!target.reasoning[0] && <option value="">{t("Provider default")}</option>}
        {efforts.map(effort => <option key={effort} value={effort}>{effort}</option>)}
      </HTMLSelect>}
      {remove && <Button variant="minimal" size="small" className="model-tier-remove" icon={<AppIcon name="close" size={13} />}
        aria-label={t("Remove {name} from {tier}", { name, tier: label })} title={t("Remove {name} from {tier}", { name, tier: label })} onClick={remove} />}
    </span>;
  };

  return <section className="adaptive-settings" aria-labelledby="adaptive-title" data-setting="models.adaptive">
    <header>
      <h2 id="adaptive-title">{t("Adaptive")}</h2>
      <p className="settings-field-help">{t("A session on Adaptive runs each turn on the model of its level. When a turn changes code, critics of another model review the change, read-only, and the session fixes what they found; a level without critics rechecks the change itself.")}</p>
    </header>
    <Switch className="settings-checkbox" checked={!!adaptive} label={t("Set up Adaptive")}
      onChange={event => void save(event.currentTarget.checked ? state.adaptive ?? (proposal ? { level: "medium", levels: proposal } : noLevels) : null)} />
    {adaptive && <div className="adaptive-fields">
      {proposal && <div className="adaptive-proposal">
        <span>{t("Tested combinations for your providers: {levels}.", {
          levels: proposal.map(level => `${t(adaptiveLevelLabels[level.level])} ${adaptiveLevelSummary(level, t)}`).join("; "),
        })}</span>
        <Button size="small" onClick={() => void save({ level: adaptive.level, levels: proposal })}>{t("Use these combinations")}</Button>
      </div>}
      <label className="adaptive-field" htmlFor="adaptive-level">
        <span>{t("New sessions on Adaptive start at")}</span>
        <HTMLSelect id="adaptive-level" value={adaptive.level} onChange={event => void save({ ...adaptive, level: event.currentTarget.value as AdaptiveLevelName })}>
          {adaptiveLevelNames.map(level => <option key={level} value={level}>{t(adaptiveLevelLabels[level])}</option>)}
        </HTMLSelect>
      </label>
      <ol className="adaptive-levels">
        {adaptiveLevelNames.map(name => {
          const level = adaptive.levels.find(existing => existing.level === name);
          const label = t(adaptiveLevelLabels[name]);
          return <li key={name} className="adaptive-level" aria-label={label}>
            <h3>{label}</h3>
            {level ? <div className="adaptive-level-models">
              <span className="adaptive-role">{t("Turns")}</span>
              {modelField(level.model, label, next => void save(withLevel(adaptive, name, existing => existing && { ...existing, model: next, choice: null })),
                () => void save(withLevel(adaptive, name, () => null)))}
              <span className="adaptive-role">{t("Critics")}</span>
              <span className="adaptive-critics">
                {level.critics.length === 0 && <span className="adaptive-recheck">{t("None: the model rechecks its change")}</span>}
                {level.critics.map((critic, index) => modelField(critic, label,
                  next => void save(withLevel(adaptive, name, existing => existing && { ...existing, critics: existing.critics.map((other, at) => at === index ? next : other) })),
                  () => void save(withLevel(adaptive, name, existing => existing && { ...existing, critics: existing.critics.filter((_, at) => at !== index) }))))}
                {level.critics.length < maximumAdaptiveCritics && <TierModelPicker providers={providers} models={models} offered={() => true} note={() => null} taken={level.critics}
                  onChoose={(provider, model) => void save(withLevel(adaptive, name, existing => existing && { ...existing, critics: [...existing.critics, targetOf(provider, model)] }))}>
                  <Button variant="minimal" size="small" icon={<AppIcon name="plus" size={14} />} aria-label={t("Add a critic to {level}", { level: label })}>{t("Add a critic")}</Button>
                </TierModelPicker>}
              </span>
            </div>
              : <TierModelPicker providers={providers} models={models} offered={() => true} note={() => null} taken={[]}
                onChoose={(provider, model) => void save(withLevel(adaptive, name, () => ({ level: name, model: targetOf(provider, model), critics: [], choice: null })))}>
                <Button variant="minimal" size="small" icon={<AppIcon name="plus" size={14} />} aria-label={t("Choose the model of {level}", { level: label })}>{t("Choose its model")}</Button>
              </TierModelPicker>}
          </li>;
        })}
      </ol>
      {adaptive.levels.length === 0 && <p role="status" className="adaptive-missing">{t("Give a level its model to use Adaptive.")}</p>}
    </div>}
    {notice && <p role="alert" className="error-text">{notice}</p>}
    <div className="adaptive-guidance">
      <h3>{t("How to choose the models")}</h3>
      <p>{t("Turns: a strong model that is inexpensive per task (it uses few tokens). Critics: models of another family, which see other mistakes; they read the code and run the tests, and never edit.")}</p>
      <p>{t("Tested in November 2026 on real repositories and Terminal-Bench: GPT-6.1 Sol for the turns and Claude Sonnet 5.5 as the critic matched Claude Opus 5.5 alone for about a quarter of its cost; a second critic, Claude Opus 5.5, added a little at the Max level.")}</p>
      <p>{t("To compare other models by their results on coding tasks and their price, see independent leaderboards such as")}{" "}
        <a href="https://artificialanalysis.ai/" target="_blank" rel="noreferrer">Artificial Analysis</a>.</p>
    </div>
  </section>;
}
