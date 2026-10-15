import { useEffect, useRef, useState, type DragEvent, type KeyboardEvent, type ReactElement } from "react";
import { Button, Checkbox, InputGroup, Menu, MenuDivider, MenuItem, PopoverNext, Switch, Tag } from "@blueprintjs/core";
import { globalConfig, type ModelCatalogModel, type ModelCatalogProvider } from "#neoastra";
import { AppIcon } from "./AppIcon";
import { matchingModels } from "./modelPicker";
import { addTierTarget, emptyTiers, moveEffort, moveTierTarget, readTierState, removeTierTarget, replaceTierTarget, setTierReasoning, startingEfforts, tierLabels,
  tierLists, tierNames, tierStore, useTiers, type TierName, type TierState, type TierStore, type TierTarget } from "./modelTiers";
import { ProviderIcon } from "./ProviderIcon";
import { useShellLanguage } from "./shellLanguage";

type Save = Pick<typeof globalConfig, "saveModelTiers">;
type Drag = Readonly<{ tier: TierName; index: number }>;
const dragType = "application/x-codealta-model-tier";

/**
 * The tiers of models on Settings > Models: three ordered lists, a row for each model of a configured provider with its
 * reasoning efforts in order. A change is saved at once; rows move by drag or with Alt+Arrow keys on their handle, and
 * change their model in place from the button of their name.
 */
export function ModelTierSettings({ epoch, providers, models, spaceId = null, api = globalConfig, store = tierStore }: {
  epoch: string; providers: readonly ModelCatalogProvider[]; models: ReadonlyMap<string, readonly ModelCatalogModel[]>;
  /** The space whose own tiers are edited, in its file; null for the global tiers of the settings. */
  spaceId?: string | null;
  api?: Save; store?: TierStore;
}) {
  const { t } = useShellLanguage();
  const scope = spaceId ? { spaceId } : null;
  const read = useTiers(epoch, scope, store);
  const [shown, setShown] = useState<TierState | null>(null);
  const [notice, setNotice] = useState<string | null>(null);
  const [over, setOver] = useState<string | null>(null);
  const saving = useRef(0);
  const drag = useRef<Drag | null>(null);
  const section = useRef<HTMLElement>(null);
  // The row whose model was changed: it is drawn anew under its new model, and its button gets the focus back.
  const refocus = useRef<string | null>(null);
  useEffect(() => { setShown(read); }, [read]);
  const state = shown ?? read ?? emptyTiers;
  useEffect(() => {
    const place = refocus.current;
    refocus.current = null;
    if (place) section.current?.querySelector<HTMLElement>(`[data-tier-model="${place}"]`)?.focus();
  }, [state]);

  async function save(next: TierState | null) {
    if (!next) return;
    const turn = ++saving.current;
    const before = state;
    setShown(next); setNotice(null);
    try {
      const reply = await api.saveModelTiers({ expectedEpoch: epoch, tiers: tierLists(next), fallback: next.fallback, spaceId, useGlobal: false },
        { timeoutMilliseconds: 30_000 });
      if (turn !== saving.current) return;
      const saved = reply.status === "ok" ? readTierState(reply) : null;
      if (saved) { store.set(epoch, saved, scope); setShown(saved); return; }
      setShown(before);
      setNotice(reply.message ?? t("The model tiers were not saved ({status}).", { status: reply.status }));
    } catch {
      if (turn !== saving.current) return;
      setShown(before);
      setNotice(t("The model tiers were not saved ({status}).", { status: "no_answer" }));
    }
  }

  const providerOf = (key: string) => providers.find(provider => provider.id.toLowerCase() === key.toLowerCase());
  const modelOf = (target: TierTarget) => models.get(providerOf(target.providerKey)?.id ?? target.providerKey)?.find(model => model.id.toLowerCase() === target.modelId.toLowerCase());
  const key = (place: Drag) => `${place.tier}:${place.index}`;
  const dropZone = (place: Drag) => ({
    "data-over": over === key(place) ? "true" : undefined,
    onDragOver: (event: DragEvent) => {
      if (!drag.current || !event.dataTransfer.types.includes(dragType)) return;
      event.preventDefault(); event.stopPropagation();
      event.dataTransfer.dropEffect = "move";
      if (over !== key(place)) setOver(key(place));
    },
    onDragLeave: (event: DragEvent) => {
      if (event.relatedTarget instanceof Node && event.currentTarget.contains(event.relatedTarget)) return;
      setOver(current => current === key(place) ? null : current);
    },
    onDrop: (event: DragEvent) => {
      const from = drag.current;
      setOver(null); drag.current = null;
      if (!from) return;
      event.preventDefault(); event.stopPropagation();
      const moved = moveTierTarget(state, from, place);
      if (moved) void save(moved);
      else if (from.tier !== place.tier) setNotice(t("The tier has this model already."));
    },
  });
  // Alt+Arrow on the handle moves the row up or down its tier, and into the tier beside it with Left and Right.
  function moveWithKeys(event: KeyboardEvent, tier: TierName, index: number) {
    if (!event.altKey || event.ctrlKey || event.metaKey) return;
    const column = tierNames.indexOf(tier);
    const to = event.key === "ArrowUp" ? { tier, index: index - 1 } : event.key === "ArrowDown" ? { tier, index: index + 2 }
      : event.key === "ArrowLeft" && column > 0 ? { tier: tierNames[column - 1], index: state.tiers[tierNames[column - 1]].length }
      : event.key === "ArrowRight" && column < tierNames.length - 1 ? { tier: tierNames[column + 1], index: state.tiers[tierNames[column + 1]].length } : null;
    if (!to) return;
    event.preventDefault();
    if (to.index < 0) return;
    const moved = moveTierTarget(state, { tier, index }, to);
    if (moved) void save(moved);
  }

  return <section ref={section} className="model-tier-settings" data-space={spaceId ? "true" : undefined}
    {...(spaceId ? { "aria-label": t("Model tiers") } : { "aria-labelledby": "model-tiers-title" })}>
    {/* The tiers of a space are shown under its own heading; the fallback stays one of the settings. */}
    {!spaceId && <header className="model-tier-header">
      <div>
        <h2 id="model-tiers-title">{t("Model tiers")}</h2>
        <p className="settings-field-help">{t("The models you prefer for complex, standard and quick work, in order.")}</p>
      </div>
      <div className="model-tier-fallback">
        <Switch className="settings-checkbox" checked={state.fallback.enabled} label={t("Fall back to the next model of the tier when a provider fails")}
          onChange={event => void save({ ...state, fallback: { ...state.fallback, enabled: event.currentTarget.checked } })} />
        {state.fallback.enabled && <span className="model-tier-triggers">
          <Checkbox inline checked={state.fallback.onQuota} label={t("Quota reached")}
            onChange={event => void save({ ...state, fallback: { ...state.fallback, onQuota: event.currentTarget.checked } })} />
          <Checkbox inline checked={state.fallback.onUnavailable} label={t("Provider unavailable")}
            onChange={event => void save({ ...state, fallback: { ...state.fallback, onUnavailable: event.currentTarget.checked } })} />
        </span>}
      </div>
    </header>}
    {notice && <p role="alert" className="error-text">{notice}</p>}
    <div className="model-tier-columns">
      {tierNames.map(tier => {
        const targets = state.tiers[tier];
        const choice = state.choices[tier];
        const label = t(tierLabels[tier]);
        return <section key={tier} className="model-tier" aria-label={label} {...dropZone({ tier, index: targets.length })}>
          <h3><span>{label}</span>
            {targets.length > 0 && <span className="model-tier-gives" title={choice ? `${choice.providerName} · ${choice.modelName}` : undefined}>
              {choice ? t("Gives {model}", { model: `${choice.modelName}${choice.reasoning ? ` · ${choice.reasoning}` : ""}` }) : t("No model ready")}</span>}
          </h3>
          <ol className="model-tier-targets">
            {targets.map((target, index) => {
              const provider = providerOf(target.providerKey);
              const model = modelOf(target);
              const name = model?.name ?? target.modelId;
              const efforts = model?.efforts ?? [];
              return <li key={`${target.providerKey}:${target.modelId}`} className="model-tier-target" draggable
                onDragStart={event => { drag.current = { tier, index }; event.dataTransfer.setData(dragType, `${tier}:${index}`); event.dataTransfer.effectAllowed = "move"; }}
                onDragEnd={() => { drag.current = null; setOver(null); }} {...dropZone({ tier, index })}>
                <span className="model-tier-grip" role="button" tabIndex={0} aria-label={t("Move {name}", { name })} title={t("Move {name}", { name })}
                  onKeyDown={event => moveWithKeys(event, tier, index)}><AppIcon name="grip" size={13} /></span>
                <ProviderIcon providerKey={provider?.id ?? target.providerKey} known={provider ? { type: provider.type, name: provider.name } : undefined} size={14} />
                {/* The model of the row is changed in its place, with the picker of "Add a model". */}
                <TierModelPicker providers={providers} models={models} taken={targets} current={target} className="model-tier-model-target"
                  onChoose={(chosen, choice) => {
                    if (sameTarget(target, chosen.id, choice.id)) return;
                    const next = replaceTierTarget(state, tier, index, { providerKey: chosen.id, modelId: choice.id }, choice);
                    if (!next) { setNotice(t("The tier has this model already.")); return; }
                    refocus.current = key({ tier, index });
                    void save(next);
                  }}>
                  <button type="button" className="model-tier-model" data-tier-model={key({ tier, index })}
                    aria-label={t("Change the model of {name}", { name })} title={`${t("Change the model of {name}", { name })}\n${target.providerKey}:${target.modelId}`}>
                    <span className="model-tier-model-text">
                      <strong>{name}</strong>
                      <span className="model-tier-provider" data-missing={provider ? undefined : "true"} title={provider ? undefined : t("Not a configured provider")}>{provider?.name ?? target.providerKey}</span>
                    </span>
                    <AppIcon name="chevronDown" size={12} className="model-tier-model-chevron" />
                  </button>
                </TierModelPicker>
                <EffortChips efforts={target.reasoning} offered={efforts}
                  onChange={reasoning => void save(setTierReasoning(state, tier, index, reasoning))} />
                <Button variant="minimal" size="small" className="model-tier-remove" icon={<AppIcon name="close" size={13} />}
                  aria-label={t("Remove {name} from {tier}", { name, tier: label })} title={t("Remove {name} from {tier}", { name, tier: label })}
                  onClick={() => void save(removeTierTarget(state, tier, index))} />
              </li>;
            })}
            {targets.length === 0 && <li className="model-tier-empty">{t("No model yet.")}</li>}
          </ol>
          <TierModelPicker providers={providers} models={models} taken={targets}
            onChoose={(provider, model) => {
              const next = addTierTarget(state, tier, { providerKey: provider.id, modelId: model.id, reasoning: startingEfforts(model) });
              if (next) void save(next); else setNotice(t("The tier has this model already."));
            }}>
            <Button variant="minimal" size="small" className="model-tier-add-button" icon={<AppIcon name="plus" size={14} />}
              aria-label={t("Add a model to {tier}", { tier: label })}>{t("Add a model")}</Button>
          </TierModelPicker>
        </section>;
      })}
    </div>
  </section>;
}

// The reasoning efforts of a target in order: a chip each, removed with its cross, moved by drag or Alt+Left/Right,
// and the efforts its model offers to add after them.
function EffortChips({ efforts, offered, onChange }: { efforts: readonly string[]; offered: readonly string[]; onChange: (efforts: readonly string[]) => void }) {
  const { t } = useShellLanguage();
  const dragged = useRef<number | null>(null);
  const addable = offered.filter(effort => !efforts.includes(effort));
  return <span className="model-tier-efforts">
    {efforts.map((effort, index) => <Tag key={effort} minimal round className="model-tier-effort" tabIndex={0} draggable
      onDragStart={event => { event.stopPropagation(); dragged.current = index; event.dataTransfer.setData("text/plain", effort); }}
      onDragOver={event => { if (dragged.current !== null) { event.preventDefault(); event.stopPropagation(); } }}
      onDrop={event => {
        const from = dragged.current; dragged.current = null;
        if (from === null) return;
        event.preventDefault(); event.stopPropagation();
        onChange(moveEffort(efforts, from, from < index ? index + 1 : index));
      }}
      onKeyDown={event => {
        if (!event.altKey || (event.key !== "ArrowLeft" && event.key !== "ArrowRight")) return;
        event.preventDefault();
        const to = event.key === "ArrowLeft" ? index - 1 : index + 2;
        if (to >= 0 && to <= efforts.length) onChange(moveEffort(efforts, index, to));
      }}
      onRemove={() => onChange(efforts.filter(value => value !== effort))}
      htmlTitle={t("Remove {effort}", { effort })}>{effort}</Tag>)}
    {addable.length > 0 && <PopoverNext placement="bottom-start" content={<Menu aria-label={t("Add an effort")}>
      {addable.map(effort => <MenuItem key={effort} text={effort} onClick={() => onChange([...efforts, effort])} />)}
    </Menu>}>
      <Button variant="minimal" size="small" className="model-tier-add-effort" icon={<AppIcon name="plus" size={12} />}
        aria-label={t("Add an effort")} title={t("Add an effort")} />
    </PopoverNext>}
  </span>;
}

const sameTarget = (target: TierTarget, provider: string, model: string) =>
  target.providerKey.toLowerCase() === provider.toLowerCase() && target.modelId.toLowerCase() === model.toLowerCase();

// The models of the enabled providers, by provider, with a search, opened by the button it is given; a model the tier
// has is not offered again, except the current one of the row it changes.
function TierModelPicker({ providers, models, taken, current, className, onChoose, children }: {
  providers: readonly ModelCatalogProvider[]; models: ReadonlyMap<string, readonly ModelCatalogModel[]>; taken: readonly TierTarget[];
  current?: TierTarget; className?: string; onChoose: (provider: ModelCatalogProvider, model: ModelCatalogModel) => void; children: ReactElement;
}) {
  const { t } = useShellLanguage();
  const [open, setOpen] = useState(false), [query, setQuery] = useState("");
  const anchor = useRef<HTMLSpanElement>(null), picker = useRef<HTMLDivElement>(null), refocus = useRef(false);
  const listed = providers.filter(provider => provider.enabled).map(provider => ({ provider, models: matchingModels(models.get(provider.id) ?? [], query) }))
    .filter(group => group.models.length > 0);
  const has = (provider: string, model: string) => taken.some(target => target !== current && sameTarget(target, provider, model));
  const isCurrent = (provider: string, model: string) => current !== undefined && sameTarget(current, provider, model);
  // Closed from inside (Escape, a model chosen), the picker gives the focus back to its button once it is gone, unless
  // the focus went elsewhere. The popover's own return is off: it notes the focus after moving it into the search.
  function close() {
    refocus.current = picker.current?.contains(document.activeElement) === true;
    setOpen(false);
  }
  function closed() {
    const button = refocus.current ? anchor.current?.querySelector<HTMLElement>("button") : null;
    refocus.current = false;
    const focus = document.activeElement;
    if (button && (!focus || focus === document.body || picker.current?.contains(focus))) button.focus();
  }
  return <span ref={anchor} className={className}><PopoverNext placement="bottom-start" isOpen={open} shouldReturnFocusOnClose={false} onClosed={closed}
    onInteraction={next => { if (!next) close(); else { setOpen(true); setQuery(""); } }}
    content={<div ref={picker} className="model-tier-add">
      <InputGroup type="search" autoFocus size="small" aria-label={t("Search models")} placeholder={t("Search models")} value={query}
        leftIcon={<AppIcon name="search" size={14} className="bp6-icon" />} onChange={event => setQuery(event.target.value)} />
      <Menu aria-label={t("Models")}>
        {listed.map(group => [
          <MenuDivider key={`${group.provider.id}-title`} title={<span className="with-logo"><ProviderIcon providerKey={group.provider.id} known={{ type: group.provider.type, name: group.provider.name }} size={13} />{group.provider.name}</span>} />,
          ...group.models.map(model => <MenuItem key={`${group.provider.id}:${model.id}`} text={model.name} label={model.name !== model.id ? model.id : undefined}
            disabled={has(group.provider.id, model.id)} active={isCurrent(group.provider.id, model.id)}
            onClick={() => { close(); onChoose(group.provider, model); }} />),
        ])}
      </Menu>
      {listed.length === 0 && <p role="status">{t("No models match this search.")}</p>}
    </div>}>
    {children}
  </PopoverNext></span>;
}
