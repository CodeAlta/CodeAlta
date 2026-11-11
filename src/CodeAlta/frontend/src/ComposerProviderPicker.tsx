import { Fragment, useContext, useId, useLayoutEffect, useMemo, useRef, useState, type CSSProperties } from "react";
import { Button, InputGroup, Menu, MenuItem, PopoverNext } from "@blueprintjs/core";
import { AppIcon } from "./AppIcon";
import { modelAfterKey, modelPickerPreferences, useModelPickerRows, type ModelPickerPreferenceStore } from "./modelPicker";
import { providerListKey, restoreProviderListPreferences, type ProviderListPreferences } from "./providerListOrder";
import { providerPickerDetail, sectionProviders } from "./providerPicker";
import { ProviderBrandsContext, ProviderIcon, providerName } from "./ProviderIcon";
import { useShellLanguage } from "./shellLanguage";

/**
 * A provider the composer offers: its key and the name Settings gives it. `isDefault` marks the default provider of the
 * settings, `firstEnabled` the provider used in its place when the settings name none.
 */
export type ComposerProviderChoice = Readonly<{ id: string; name: string; isDefault?: boolean; firstEnabled?: boolean }>;

function storedProviderList(): ProviderListPreferences {
  return restoreProviderListPreferences(() => typeof localStorage === "undefined" ? null : localStorage.getItem(providerListKey));
}

/**
 * The provider of the next Send as a searchable list, like the list of the models: the focus stays in the search box,
 * the arrows move through the providers that match, and Enter takes the active one. The favorite providers of Settings
 * come first, the others after them, each group by name; each provider has its logo, and its key and type beside its
 * name. A saved provider that is not offered is named on the button, never in the list; while the providers are read,
 * the button names the provider in use and waits.
 */
export function ComposerProviderPicker({ id, value, providers, disabled = false, loading = false, unlisted, title, onChange, onOpen, preferences = modelPickerPreferences,
  readFavorites = storedProviderList }: {
  id: string; value: string | null; providers: readonly ComposerProviderChoice[];
  disabled?: boolean; loading?: boolean;
  /** What the button says after a provider that is not offered; "Unverified" without it. */
  unlisted?: string;
  title?: string; onChange: (providerId: string) => void;
  /** Called when the list opens, to read the providers again when they could not be read. */
  onOpen?: () => void;
  /** How many rows the list shows: the setting of the list of the models. */
  preferences?: ModelPickerPreferenceStore;
  /** The favorite providers of Settings, read each time the list opens. */
  readFavorites?: () => ProviderListPreferences;
}) {
  const { t } = useShellLanguage();
  const rows = useModelPickerRows(preferences);
  const brands = useContext(ProviderBrandsContext);
  const [open, setOpen] = useState(false), [query, setQuery] = useState(""), [active, setActive] = useState(0);
  const [favorites, setFavorites] = useState(readFavorites);
  const list = useRef<HTMLUListElement>(null), search = useRef<HTMLInputElement>(null);
  const listId = useId();
  const choices = useMemo(() => providers.map(provider => ({ ...provider, type: brands.get(provider.id.toLowerCase())?.type ?? null })), [providers, brands]);
  const sections = useMemo(() => sectionProviders(choices, query, favorites), [choices, query, favorites]);
  const shown = useMemo(() => [...sections.favorites, ...sections.others], [sections]);
  // The heading of the others stands after the favorites, when both are listed.
  const othersAt = sections.favorites.length > 0 && sections.others.length > 0 ? sections.favorites.length : -1;
  const display = (provider: ComposerProviderChoice) => {
    const name = provider.name.trim() || provider.id;
    return provider.isDefault ? t("{name} (default)", { name }) : provider.firstEnabled ? t("{name} (first enabled)", { name }) : name;
  };
  const current = providers.find(provider => provider.id === value);
  const named = value ? providerName(brands, value) ?? value : null;
  const label = current ? display(current) : loading ? named ?? t("Loading…") : value ? `${value} · ${unlisted ?? t("Unverified")}` : t("No provider");
  const usable = !disabled && !loading;
  const option = (index: number) => `${listId}-${index}`;
  const reveal = () => list.current?.querySelector(".bp6-active")?.scrollIntoView({ block: "nearest" });
  // The active provider stays in view while the arrows move it and while the search changes what is listed.
  useLayoutEffect(() => { if (open) reveal(); }, [open, active, shown]);
  function choose(provider: ComposerProviderChoice | undefined) {
    if (!provider) return;
    setOpen(false);
    if (provider.id !== value) onChange(provider.id);
  }
  const content = <div className="composer-model-picker composer-provider-picker" style={{ "--model-picker-rows": rows } as CSSProperties}>
    <InputGroup type="search" inputRef={search} role="combobox" aria-label={t("Search providers")} placeholder={t("Search providers")} value={query}
      aria-expanded="true" aria-controls={listId} aria-autocomplete="list" aria-activedescendant={shown.length ? option(active) : undefined}
      leftIcon={<AppIcon name="search" size={14} className="bp6-icon" />}
      onChange={event => { setQuery(event.target.value); setActive(0); }}
      onKeyDown={event => {
        if (event.key === "Enter") { event.preventDefault(); choose(shown[active]); return; }
        const next = modelAfterKey(event.key, active, shown.length, rows);
        if (next === null) return;
        event.preventDefault(); setActive(next);
      }} />
    <Menu ulRef={list} id={listId} role="listbox" aria-label={t("Providers")}>
      {shown.map((provider, index) => <Fragment key={provider.id}>
        {index === 0 && sections.favorites.length > 0 && <li role="separator" className="composer-model-separator">{t("Favorites")}</li>}
        {index === othersAt && <li role="separator" className="composer-model-separator">{t("Other providers")}</li>}
        <MenuItem roleStructure="listoption" data-provider-id={provider.id}
          // The option is the list item, which is what the search box names as its active descendant.
          ref={item => { if (item) item.id = option(index); }}
          active={index === active} selected={provider.id === value} shouldDismissPopover={false}
          icon={<ProviderIcon providerKey={provider.id} size={14} />}
          text={display(provider)} label={providerPickerDetail(provider) ?? undefined} htmlTitle={[provider.id, provider.type].filter(Boolean).join(" · ")}
          onMouseEnter={() => setActive(index)} onClick={() => choose(provider)} />
      </Fragment>)}
    </Menu>
    {shown.length === 0 && <p role="status">{t(providers.length ? "No providers match this search." : "No provider")}</p>}
  </div>;
  // The search box has the focus as soon as the list is in the page, so that what is typed at once is searched.
  const focusSearch = () => { if (document.activeElement !== search.current) search.current?.focus({ preventScroll: true }); };
  return <PopoverNext className="composer-model-target" fill placement="bottom-start" matchTargetWidth isOpen={open && usable} content={content} onOpening={focusSearch}
    onInteraction={next => {
      // The list opens on the provider in use, with nothing searched, and with the favorites Settings has now.
      if (next && !open) {
        onOpen?.();
        const stored = readFavorites(), all = sectionProviders(choices, "", stored);
        setFavorites(stored); setQuery(""); setActive(Math.max(0, [...all.favorites, ...all.others].findIndex(provider => provider.id === value)));
      }
      setOpen(next);
    }} onOpened={() => { focusSearch(); reveal(); }}>
    <Button id={id} className="composer-model-select composer-provider-select" alignText="start" aria-haspopup="listbox" aria-expanded={open && usable}
      aria-label={`${t("Provider")}: ${label}`} title={title} disabled={!usable}
      endIcon={<AppIcon name="chevronDown" size={14} />}>{label}</Button>
  </PopoverNext>;
}
