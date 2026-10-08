import { useMemo, useState } from "react";
import { Button, InputGroup, PopoverNext } from "@blueprintjs/core";
import { AppIcon, type IconName } from "./AppIcon";
import { BrandIcon, brandIconNames, brandTitle, isBrandIcon } from "./BrandIcon";
import { brandColor, type Brand } from "./brands";
import { Logo } from "./ProviderIcon";
import { useShellLanguage } from "./shellLanguage";
import { isSymbolIcon, symbolIconNames } from "./symbolIcons";

/**
 * The icon of a provider: the one found for it by itself, or one chosen among the logos of the brands and the
 * general icons. The button shows the icon the provider has now; the list is filtered by typing. What is not a
 * provider and has an icon (a space) is given one the same way, with the general icons first.
 */
export function ProviderIconPicker({ id, value, automatic, shown, disabled, onChange, fallback = "provider", symbolsFirst = false, compact = false }: {
  id: string;
  /** The id of the chosen icon; blank when the icon is found by itself. */
  value: string;
  /** The icon found for the provider when none is chosen. */
  automatic: Brand;
  /** The icon the provider has now, with its color. */
  shown: Brand;
  disabled?: boolean;
  onChange: (icon: string) => void;
  /** The icon drawn where neither a logo nor a general icon is chosen. */
  fallback?: IconName;
  /** Whether the general icons are listed before the logos of the brands. */
  symbolsFirst?: boolean;
  /** Whether the button shows the icon alone, without its name. */
  compact?: boolean;
}) {
  const { t } = useShellLanguage();
  const [open, setOpen] = useState(false);
  const [filter, setFilter] = useState("");
  const wanted = filter.trim().toLowerCase();
  const brands = useMemo(() => brandIconNames.filter(name => !wanted || name.includes(wanted) || brandTitle(name).toLowerCase().includes(wanted)), [wanted]);
  const symbols = useMemo(() => symbolIconNames.filter(name => !wanted || name.includes(wanted)), [wanted]);
  const choose = (icon: string) => { onChange(icon); setOpen(false); setFilter(""); };
  const label = isBrandIcon(value) ? brandTitle(value) : value || t("Automatic");
  const brandList = brands.length > 0 && <><h6>{t("Brands")}</h6><div className="provider-icon-grid">
    {brands.map(name => <Button key={name} variant="minimal" active={value === name} title={brandTitle(name)} aria-label={brandTitle(name)}
      icon={<BrandIcon name={name} size={20} />} onClick={() => choose(name)} />)}</div></>;
  const symbolList = symbols.length > 0 && <><h6>{t("General icons")}</h6><div className="provider-icon-grid">
    {symbols.map(name => <Button key={name} variant="minimal" active={value === name} title={name} aria-label={name}
      icon={<Logo brand={{ icon: null, symbol: name }} size={20} fallback={fallback} />} onClick={() => choose(name)} />)}</div></>;
  const list = <div className="provider-icon-list" role="group" aria-label={t("Icon")}>
    <InputGroup autoFocus type="search" value={filter} placeholder={t("Search")} aria-label={t("Search")} onChange={event => setFilter(event.target.value)} />
    <div className="provider-icon-scroll">
      {!wanted && <Button variant="minimal" alignText="start" fill className="provider-icon-automatic" active={!value}
        icon={<Logo brand={automatic} size={18} fallback={fallback} />} onClick={() => choose("")}>{t("Automatic")}</Button>}
      {symbolsFirst ? <>{symbolList}{brandList}</> : <>{brandList}{symbolList}</>}
      {brands.length + symbols.length === 0 && <p className="bp6-text-muted">{t("No icon matches.")}</p>}
    </div>
  </div>;
  return <PopoverNext isOpen={open} onInteraction={next => setOpen(next)} placement="bottom-start" content={list} popoverClassName="provider-icon-popover">
    {compact ? <Button id={id} disabled={disabled} className="provider-icon-button provider-icon-compact" aria-haspopup="dialog" aria-label={t("Icon")} title={t("Icon")}
      icon={<Logo brand={shown} size={18} fallback={fallback} />} />
    : <Button id={id} fill alignText="start" disabled={disabled} className="provider-icon-button" aria-haspopup="dialog"
      icon={<Logo brand={shown} size={18} fallback={fallback} />} endIcon={<AppIcon name="chevronDown" size={14} />}>
      {/* An id the file names and this version does not draw is kept, and shown as it is written. */}
      {label}{value && !isBrandIcon(value) && !isSymbolIcon(value) ? ` (${t("Unknown")})` : ""}</Button>}
  </PopoverNext>;
}

/** The color of the icon of a provider: a color of the palette of the system, or `#rgb` / `#rrggbb` typed; blank for the colors of the icon. */
export function ProviderColorInput({ id, value, disabled, onChange, placeholder }: {
  id: string; value: string; disabled?: boolean; onChange: (color: string) => void;
  /** What the empty field says; the colors of the icon when nothing is given. */
  placeholder?: string;
}) {
  const { t } = useShellLanguage();
  const valid = brandColor(value);
  // The palette of the system takes six digits: a short color is spelled out for it.
  const six = valid ? (valid.length === 4 ? `#${[...valid.slice(1)].map(digit => digit + digit).join("")}` : valid).toLowerCase() : "#888888";
  return <InputGroup id={id} value={value} disabled={disabled} maxLength={7} spellCheck={false} placeholder={placeholder ?? t("Colors of the icon")} intent={value.trim() && !valid ? "danger" : undefined}
    onChange={event => onChange(event.target.value)}
    leftElement={<input type="color" className="provider-color-swatch" value={six} disabled={disabled} aria-label={t("Choose a color")} title={t("Choose a color")}
      data-unset={valid ? undefined : "true"} onChange={event => onChange(event.target.value)} />}
    rightElement={value ? <Button variant="minimal" size="small" disabled={disabled} icon={<AppIcon name="close" size={14} />} aria-label={t("Clear")} title={t("Clear")}
      onClick={() => onChange("")} /> : undefined} />;
}
