import { useId, useLayoutEffect, useMemo, useRef, useState, type CSSProperties } from "react";
import { Button, InputGroup, Menu, MenuItem, PopoverNext } from "@blueprintjs/core";
import { AppIcon } from "./AppIcon";
import { matchingModels, modelAfterKey, modelPickerPreferences, useModelPickerRows, type ModelPickerChoice, type ModelPickerPreferenceStore } from "./modelPicker";
import { ModelIcon } from "./ProviderIcon";
import { useShellLanguage } from "./shellLanguage";

/** A tier of models as the list of the composer offers it: its name, and the model it gives now (null when none is ready). */
export type ComposerTier = Readonly<{ tier: string; label: string; detail: string | null; title?: string | null }>;

/**
 * Adaptive as the list of the composer offers it: what its levels run on, or that it is not set up (choosing it then
 * offers to). A level whose model is not ready has no detail.
 */
export type ComposerAdaptive = Readonly<{ ready: boolean; detail: string; title?: string | null;
  levels?: readonly Readonly<{ level: string; label: string; detail: string | null; title?: string | null }>[] }>;

/**
 * The model of the next Send as a searchable list: the focus stays in the search box, the arrows move through the
 * models that match, and Enter takes the active one. The list shows the number of models chosen in Settings and scrolls
 * for the rest. A saved model the provider does not list is named on the button, never in the list.
 */
export function ComposerModelPicker({ id, value, models, providerKey, disabled = false, loading = false, title, onChange, preferences = modelPickerPreferences,
  tiers, currentTier = null, onChooseTier, adaptive = null, onChooseAdaptive, currentAdaptiveLevel = null }: {
  id: string; value: string | null; models: readonly ModelPickerChoice[]; providerKey?: string | null;
  disabled?: boolean; loading?: boolean; title?: string; onChange: (modelId: string) => void;
  preferences?: ModelPickerPreferenceStore;
  /** The tiers of the settings, shown at the top of the list with the model each gives; none when the user set none. */
  tiers?: readonly ComposerTier[];
  /** The tier the session runs on, `adaptive` or `adaptive:<level>` on Adaptive, or null for a model chosen by hand. */
  currentTier?: string | null;
  onChooseTier?: (tier: string) => void;
  /** Adaptive, offered after the tiers; none where it cannot be chosen. */
  adaptive?: ComposerAdaptive | null;
  /** Chooses Adaptive at a level, or at the usual one without. */
  onChooseAdaptive?: (level?: string) => void;
  /** The level of Adaptive the session runs on, when it is on Adaptive. */
  currentAdaptiveLevel?: string | null;
}) {
  const { t } = useShellLanguage();
  const rows = useModelPickerRows(preferences);
  const [open, setOpen] = useState(false), [query, setQuery] = useState(""), [active, setActive] = useState(0);
  const list = useRef<HTMLUListElement>(null), search = useRef<HTMLInputElement>(null);
  const listId = useId();
  const onAdaptive = currentTier === "adaptive" || !!currentTier?.startsWith("adaptive:");
  const shown = useMemo(() => matchingModels(models, query), [models, query]);
  const current = models.find(model => model.id === value);
  const label = current?.name ?? (value ? loading ? t("Loading…") : `${value} · ${t("Unverified")}` : t(loading ? "Loading…" : "No model"));
  const usable = !disabled && !loading;
  const option = (index: number) => `${listId}-${index}`;
  const reveal = () => list.current?.querySelector(".bp6-active")?.scrollIntoView({ block: "nearest" });
  // The active model stays in view while the arrows move it and while the search changes what is listed.
  useLayoutEffect(() => { if (open) reveal(); }, [open, active, shown]);
  function choose(model: ModelPickerChoice | undefined) {
    if (!model) return;
    setOpen(false);
    if (model.id !== value) onChange(model.id);
  }
  const content = <div className="composer-model-picker" style={{ "--model-picker-rows": rows } as CSSProperties}>
    {(tiers && tiers.length > 0 && onChooseTier || adaptive && onChooseAdaptive) && <div className="composer-tiers" role="group" aria-label={t("Tiers")}
      // A column per tier, Adaptive on a line of its own under them.
      style={{ gridTemplateColumns: `repeat(${Math.max(1, onChooseTier ? tiers?.length ?? 0 : 0)}, minmax(0, 1fr))` }}>
      {onChooseTier && tiers?.map(tier => <Button key={tier.tier} variant="minimal" size="small" className="composer-tier" active={tier.tier === currentTier}
        aria-pressed={tier.tier === currentTier} disabled={!tier.detail} title={tier.title ?? undefined}
        onClick={() => { setOpen(false); onChooseTier(tier.tier); }}>
        <span className="composer-tier-name">{tier.label}</span>
        <span className="composer-tier-model">{tier.detail ?? t("No model ready")}</span>
      </Button>)}
      {/* Adaptive that is not set up stays offered: choosing it says so and leads to its settings. Set up, it offers its levels. */}
      {adaptive && onChooseAdaptive && (adaptive.ready && adaptive.levels && adaptive.levels.length > 0
        ? <div className="composer-adaptive-levels" role="group" aria-label={t("Adaptive")} style={{ gridTemplateColumns: `auto repeat(${adaptive.levels.length}, minmax(0, 1fr))` }}>
          <span className="composer-adaptive-name" title={t("CodeAlta chooses the models of each turn")}>{t("Adaptive")}</span>
          {adaptive.levels.map(level => {
            const chosen = onAdaptive && (currentAdaptiveLevel ?? null) === level.level;
            return <Button key={level.level} variant="minimal" size="small" className="composer-tier composer-adaptive" data-level={level.level} active={chosen}
              aria-pressed={chosen} disabled={!level.detail} title={level.title ?? undefined} onClick={() => { setOpen(false); onChooseAdaptive(level.level); }}>
              <span className="composer-tier-name">{level.label}</span>
              <span className="composer-tier-model">{level.detail ?? t("No model ready")}</span>
            </Button>;
          })}
        </div>
        : <Button variant="minimal" size="small" className="composer-tier composer-adaptive" active={onAdaptive}
          aria-pressed={onAdaptive} data-ready={adaptive.ready ? "true" : "false"}
          title={adaptive.title ?? t("CodeAlta chooses the models of each turn")} onClick={() => { setOpen(false); onChooseAdaptive(); }}>
          <span className="composer-tier-name">{t("Adaptive")}</span>
          <span className="composer-tier-model">{adaptive.detail}</span>
        </Button>)}
    </div>}
    <InputGroup type="search" inputRef={search} role="combobox" aria-label={t("Search models")} placeholder={t("Search models")} value={query}
      aria-expanded="true" aria-controls={listId} aria-autocomplete="list" aria-activedescendant={shown.length ? option(active) : undefined}
      leftIcon={<AppIcon name="search" size={14} className="bp6-icon" />}
      onChange={event => { setQuery(event.target.value); setActive(0); }}
      onKeyDown={event => {
        if (event.key === "Enter") { event.preventDefault(); choose(shown[active]); return; }
        const next = modelAfterKey(event.key, active, shown.length, rows);
        if (next === null) return;
        event.preventDefault(); setActive(next);
      }} />
    <Menu ulRef={list} id={listId} role="listbox" aria-label={t("Models")}>
      {shown.map((model, index) => <MenuItem key={model.id} roleStructure="listoption" data-model-id={model.id}
        // The option is the list item, which is what the search box names as its active descendant.
        ref={item => { if (item) item.id = option(index); }}
        active={index === active} selected={model.id === value} shouldDismissPopover={false}
        icon={<ModelIcon modelId={model.id} providerKey={providerKey} size={14} />}
        text={model.name} label={model.name !== model.id ? model.id : undefined} htmlTitle={model.id}
        onMouseEnter={() => setActive(index)} onClick={() => choose(model)} />)}
    </Menu>
    {shown.length === 0 && <p role="status">{t(models.length ? "No models match this search." : "No model")}</p>}
  </div>;
  // The search box has the focus as soon as the list is in the page, so that what is typed at once is searched.
  const focusSearch = () => { if (document.activeElement !== search.current) search.current?.focus({ preventScroll: true }); };
  return <PopoverNext className="composer-model-target" fill placement="bottom-start" matchTargetWidth isOpen={open && usable} content={content} onOpening={focusSearch}
    onInteraction={next => {
      // The list opens on the model in use, with nothing searched.
      if (next && !open) { setQuery(""); setActive(Math.max(0, models.findIndex(model => model.id === value))); }
      setOpen(next);
    }} onOpened={() => { focusSearch(); reveal(); }}>
    <Button id={id} className="composer-model-select" data-model-selector alignText="start" aria-haspopup="listbox" aria-expanded={open && usable}
      aria-label={`${t("Model")}: ${label}`} title={title} disabled={!usable}
      endIcon={<AppIcon name="chevronDown" size={14} />}>{label}</Button>
  </PopoverNext>;
}
