import { useId, useLayoutEffect, useMemo, useRef, useState, type CSSProperties } from "react";
import { Button, InputGroup, Menu, MenuItem, PopoverNext } from "@blueprintjs/core";
import { AppIcon } from "./AppIcon";
import { matchingModels, modelAfterKey, modelPickerPreferences, useModelPickerRows, type ModelPickerChoice, type ModelPickerPreferenceStore } from "./modelPicker";
import { ModelIcon } from "./ProviderIcon";
import { useShellLanguage } from "./shellLanguage";

/** A tier of models as the list of the composer offers it: its name, and the model it gives now (null when none is ready). */
export type ComposerTier = Readonly<{ tier: string; label: string; detail: string | null; title?: string | null }>;

/**
 * The model of the next Send as a searchable list: the focus stays in the search box, the arrows move through the
 * models that match, and Enter takes the active one. The list shows the number of models chosen in Settings and scrolls
 * for the rest. A saved model the provider does not list is named on the button, never in the list.
 */
export function ComposerModelPicker({ id, value, models, providerKey, disabled = false, loading = false, title, onChange, preferences = modelPickerPreferences,
  tiers, currentTier = null, onChooseTier }: {
  id: string; value: string | null; models: readonly ModelPickerChoice[]; providerKey?: string | null;
  disabled?: boolean; loading?: boolean; title?: string; onChange: (modelId: string) => void;
  preferences?: ModelPickerPreferenceStore;
  /** The tiers of the settings, shown at the top of the list with the model each gives; none when the user set none. */
  tiers?: readonly ComposerTier[];
  /** The tier the session runs on, or null for a model chosen by hand. */
  currentTier?: string | null;
  onChooseTier?: (tier: string) => void;
}) {
  const { t } = useShellLanguage();
  const rows = useModelPickerRows(preferences);
  const [open, setOpen] = useState(false), [query, setQuery] = useState(""), [active, setActive] = useState(0);
  const list = useRef<HTMLUListElement>(null), search = useRef<HTMLInputElement>(null);
  const listId = useId();
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
    {tiers && tiers.length > 0 && onChooseTier && <div className="composer-tiers" role="group" aria-label={t("Tiers")}>
      {tiers.map(tier => <Button key={tier.tier} variant="minimal" size="small" className="composer-tier" active={tier.tier === currentTier}
        aria-pressed={tier.tier === currentTier} disabled={!tier.detail} title={tier.title ?? undefined}
        onClick={() => { setOpen(false); onChooseTier(tier.tier); }}>
        <span className="composer-tier-name">{tier.label}</span>
        <span className="composer-tier-model">{tier.detail ?? t("No model ready")}</span>
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
