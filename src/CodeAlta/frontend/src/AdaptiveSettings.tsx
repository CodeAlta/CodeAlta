import { useState, type ReactElement } from "react";
import { Button, Callout, HTMLSelect, Radio, Switch } from "@blueprintjs/core";
import { globalConfig, type AdaptiveLevelSetting, type ModelCatalogModel, type ModelCatalogProvider } from "#neoastra";
import { AppIcon } from "./AppIcon";
import { adaptiveLevelLabels, adaptiveLevelNames, maximumAdaptiveCritics, readTierState, startingEfforts,
  type AdaptiveChoice, type AdaptiveLevel, type AdaptiveLevelName, type TierState, type TierStore, type TierTarget } from "./modelTiers";
import { TierModelPicker } from "./ModelTierSettings";
import { ProviderIcon } from "./ProviderIcon";
import { useShellLanguage } from "./shellLanguage";

type SaveAdaptive = Pick<typeof globalConfig, "saveAdaptive">;

const noLevels: AdaptiveChoice = Object.freeze({ level: "medium", levels: [] });

// What each level is for, in a few words.
const levelPurposes = Object.freeze({
  low: "Fast and inexpensive", medium: "The usual level", high: "Harder work", max: "When it must be right",
} as const satisfies Record<AdaptiveLevelName, string>);

// A level as the host saves it.
const toSetting = (level: AdaptiveLevel): AdaptiveLevelSetting => ({
  level: level.level, model: { ...level.model, reasoning: [...level.model.reasoning] },
  critics: level.critics.map(critic => ({ ...critic, reasoning: [...critic.reasoning] })), choice: null, criticNames: null,
});

const sameTarget = (left: TierTarget, right: TierTarget) => left.providerKey.toLowerCase() === right.providerKey.toLowerCase()
  && left.modelId.toLowerCase() === right.modelId.toLowerCase() && (left.reasoning[0] ?? "").toLowerCase() === (right.reasoning[0] ?? "").toLowerCase();
const sameLevels = (left: readonly AdaptiveLevel[], right: readonly AdaptiveLevel[]) => left.length === right.length
  && left.every((level, index) => level.level === right[index].level && sameTarget(level.model, right[index].model)
    && level.critics.length === right[index].critics.length && level.critics.every((critic, at) => sameTarget(critic, right[index].critics[at])));

/**
 * Adaptive on Settings > Models, under the tiers: whether it is set up, the levels CodeAlta recommends for the providers
 * that are ready, and a grid of the four levels, each with the model of its turns and its effort, the critics that review
 * its changes of code, and whether new sessions start at it. Saved at once.
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
  const nameOf = (target: TierTarget) => modelOf(target)?.name ?? target.modelId;
  // A level changed, added, or taken out (null).
  const withLevel = (current: AdaptiveChoice, level: AdaptiveLevelName, change: (existing: AdaptiveLevel | undefined) => AdaptiveLevel | null | undefined): AdaptiveChoice => {
    const changed = change(current.levels.find(existing => existing.level === level));
    const others = current.levels.filter(existing => existing.level !== level);
    return { ...current, levels: (changed ? [...others, changed] : others).sort((left, right) => adaptiveLevelNames.indexOf(left.level) - adaptiveLevelNames.indexOf(right.level)) };
  };
  const targetOf = (provider: ModelCatalogProvider, model: ModelCatalogModel): TierTarget =>
    ({ providerKey: provider.id, modelId: model.id, reasoning: startingEfforts(model).slice(0, 1) });
  const picker = (current: TierTarget | undefined, taken: readonly TierTarget[], onChoose: (next: TierTarget) => void, button: ReactElement) =>
    <TierModelPicker providers={providers} models={models} offered={() => true} note={() => null} taken={taken} current={current} className="adaptive-picker"
      onChoose={(provider, model) => onChoose(targetOf(provider, model))}>{button}</TierModelPicker>;
  const effortSelect = (target: TierTarget, label: string, change: (next: TierTarget) => void) => {
    const efforts = modelOf(target)?.efforts ?? [];
    return efforts.length === 0 ? <span className="adaptive-effort-none">—</span>
      : <HTMLSelect minimal className="adaptive-effort" aria-label={t("Reasoning effort of {name}", { name: label })}
        value={target.reasoning[0] ?? ""} onChange={event => change({ ...target, reasoning: event.currentTarget.value ? [event.currentTarget.value] : [] })}>
        {!target.reasoning[0] && <option value="">{t("Provider default")}</option>}
        {efforts.map(effort => <option key={effort} value={effort}>{effort}</option>)}
      </HTMLSelect>;
  };
  // The model of a row: its provider and name, and a chevron; it opens the list of models.
  const modelButton = (target: TierTarget, label: string) => {
    const provider = providerOf(target.providerKey);
    return <button type="button" className="adaptive-model-button" aria-label={t("Change the model of {name}", { name: label })} title={`${target.providerKey}:${target.modelId}`}>
      <ProviderIcon providerKey={provider?.id ?? target.providerKey} known={provider ? { type: provider.type, name: provider.name } : undefined} size={14} />
      <span className="adaptive-model-text">
        <strong>{nameOf(target)}</strong>
        <span className="model-tier-provider" data-missing={provider ? undefined : "true"} title={provider ? undefined : t("Not a configured provider")}>{provider?.name ?? target.providerKey}</span>
      </span>
      <AppIcon name="chevronDown" size={12} className="model-tier-model-chevron" />
    </button>;
  };

  const usingProposal = !!adaptive && !!proposal && sameLevels(adaptive.levels, proposal);
  return <section className="adaptive-settings" aria-labelledby="adaptive-title" data-setting="models.adaptive">
    <header>
      <h2 id="adaptive-title">{t("Adaptive")}</h2>
      <p className="settings-field-help">{t("A session on Adaptive runs each turn on the model of its level. When a turn changes code, critics of another model review the change, read-only, and the session fixes what they found; a level without critics rechecks the change itself.")}</p>
    </header>
    <Switch className="settings-checkbox" checked={!!adaptive} label={t("Set up Adaptive")}
      onChange={event => void save(event.currentTarget.checked ? state.adaptive ?? (proposal ? { level: "medium", levels: proposal } : noLevels) : null)} />
    {adaptive && <div className="adaptive-body">
      {proposal && usingProposal && <p className="adaptive-proposal-used"><AppIcon name="check" size={14} />{t("Your levels are the recommended ones, tested in November 2026.")}</p>}
      {proposal && !usingProposal && <Callout className="adaptive-proposal" intent="primary" compact
        icon={<AppIcon name="star" size={16} />} title={t("Recommended for your accounts")}>
        <p>{t("Tested in November 2026 with the models your providers offer. Use them as they are, or as a start.")}</p>
        <dl className="adaptive-proposal-levels">
          {proposal.map(level => <div key={level.level}>
            <dt>{t(adaptiveLevelLabels[level.level])}</dt>
            <dd>{[`${level.choice?.modelName ?? level.model.modelId}${level.model.reasoning[0] ? ` ${level.model.reasoning[0]}` : ""}`,
              level.critics.length > 0 ? t("Review by {models}", { models: (level.criticNames ?? level.critics.map(critic => critic.modelId)).join(", ") }) : t("Recheck")].join(" · ")}</dd>
          </div>)}
        </dl>
        <Button intent="primary" size="small" onClick={() => void save({ level: adaptive.level, levels: proposal })}>{t("Use the recommended levels")}</Button>
      </Callout>}
      <div className="adaptive-grid" role="table" aria-label={t("Levels of Adaptive")}>
        <div className="adaptive-row adaptive-head" role="row">
          <span role="columnheader">{t("Level")}</span>
          <span role="columnheader">{t("Model of the turns")}</span>
          <span role="columnheader">{t("Effort")}</span>
          <span role="columnheader">{t("Critics of each change")}</span>
          <span role="columnheader" className="adaptive-center">{t("Default")}</span>
          <span role="columnheader"><span className="bp6-text-muted">&nbsp;</span></span>
        </div>
        {adaptiveLevelNames.map(name => {
          const level = adaptive.levels.find(existing => existing.level === name);
          const label = t(adaptiveLevelLabels[name]);
          const isDefault = adaptive.level === name;
          const update = (change: (existing: AdaptiveLevel) => AdaptiveLevel) => void save(withLevel(adaptive, name, existing => existing && change(existing)));
          return <div key={name} className="adaptive-row" role="row" data-level={name} data-set={level ? "true" : "false"} aria-label={label}>
            <span className="adaptive-level-name" role="rowheader"><strong>{label}</strong><span className="bp6-text-muted">{t(levelPurposes[name])}</span></span>
            <span className="adaptive-cell" role="cell" data-label={t("Model of the turns")}>
              {level ? picker(level.model, [], next => update(existing => ({ ...existing, model: next, choice: null })), modelButton(level.model, `${label} · ${nameOf(level.model)}`))
                : picker(undefined, [], next => void save(withLevel(adaptive, name, () => ({ level: name, model: next, critics: [], choice: null }))),
                  <Button variant="outlined" size="small" icon={<AppIcon name="plus" size={14} />} aria-label={t("Choose the model of {level}", { level: label })}>{t("Choose a model")}</Button>)}
            </span>
            <span className="adaptive-cell" role="cell" data-label={t("Effort")}>
              {level ? effortSelect(level.model, `${label} · ${nameOf(level.model)}`, next => update(existing => ({ ...existing, model: next, choice: null }))) : null}
            </span>
            <span className="adaptive-cell adaptive-critics" role="cell" data-label={t("Critics")}>
              {level && level.critics.length === 0 && <span className="adaptive-recheck">{t("None: rechecks its change")}</span>}
              {level?.critics.map((critic, index) => {
                const criticLabel = `${label} · ${nameOf(critic)}`;
                return <span key={`${critic.providerKey}:${critic.modelId}`} className="adaptive-critic">
                  {picker(critic, level.critics, next => update(existing => ({ ...existing, critics: existing.critics.map((other, at) => at === index ? next : other) })),
                    modelButton(critic, criticLabel))}
                  {effortSelect(critic, criticLabel, next => update(existing => ({ ...existing, critics: existing.critics.map((other, at) => at === index ? next : other) })))}
                  <Button variant="minimal" size="small" className="adaptive-critic-remove" icon={<AppIcon name="close" size={12} />}
                    aria-label={t("Remove {name} from {tier}", { name: nameOf(critic), tier: label })} title={t("Remove {name} from {tier}", { name: nameOf(critic), tier: label })}
                    onClick={() => update(existing => ({ ...existing, critics: existing.critics.filter((_, at) => at !== index) }))} />
                </span>;
              })}
              {level && level.critics.length < maximumAdaptiveCritics && picker(undefined, level.critics,
                next => update(existing => ({ ...existing, critics: [...existing.critics, next] })),
                <Button variant="minimal" size="small" className="adaptive-add-critic" icon={<AppIcon name="plus" size={13} />}
                  aria-label={t("Add a critic to {level}", { level: label })}>{t("Add a critic")}</Button>)}
            </span>
            <span className="adaptive-cell adaptive-center" role="cell" data-label={t("Default")}>
              <Radio className="adaptive-default" checked={isDefault} aria-label={t("Start new sessions at {level}", { level: label })}
                title={t("Start new sessions at {level}", { level: label })} onChange={() => { if (!isDefault) void save({ ...adaptive, level: name }); }} />
            </span>
            <span className="adaptive-cell" role="cell">
              {level && <Button variant="minimal" size="small" icon={<AppIcon name="trash" size={13} />}
                aria-label={t("Clear the level {level}", { level: label })} title={t("Clear the level {level}", { level: label })}
                onClick={() => void save(withLevel(adaptive, name, () => null))} />}
            </span>
          </div>;
        })}
      </div>
      {adaptive.levels.length === 0 && <p role="status" className="adaptive-missing">{t("Give a level its model to use Adaptive.")}</p>}
    </div>}
    {notice && <p role="alert" className="error-text">{notice}</p>}
    <details className="adaptive-guidance">
      <summary>{t("How to choose the models")}</summary>
      <p>{t("Turns: a strong model that is inexpensive per task (it uses few tokens). Critics: models of another family, which see other mistakes; they read the code and run the tests, and never edit.")}</p>
      <p>{t("Tested in November 2026 on real repositories and Terminal-Bench: GPT-6.1 Sol for the turns and Claude Sonnet 5.5 as the critic matched Claude Opus 5.5 alone for about a quarter of its cost; a second critic, Claude Opus 5.5, added a little at the Max level.")}</p>
      <p>{t("To compare other models by their results on coding tasks and their price, see independent leaderboards such as")}{" "}
        <a href="https://artificialanalysis.ai/" target="_blank" rel="noreferrer">Artificial Analysis</a>.</p>
    </details>
  </section>;
}
