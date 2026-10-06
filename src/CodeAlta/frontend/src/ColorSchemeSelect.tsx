import type { KeyboardEvent } from "react";
import { Button, Menu, MenuItem, PopoverNext } from "@blueprintjs/core";
import { AppIcon } from "./AppIcon";
import { colorSchemeOf, colorSchemes, schemePalette, schemeSwatch, type ColorVariant } from "./colorSchemes";
import type { Palette } from "./colorSchemes.gen";
import { useShellLanguage } from "./shellLanguage";

/** The small picture of a palette in a picker: its background, with its text, its tint and its accent as dots. */
export function ColorSchemeSwatch({ palette, variant }: { palette: Palette; variant: ColorVariant }) {
  const swatch = schemeSwatch(palette, variant);
  return <span className="color-scheme-swatch" aria-hidden="true" style={{ background: swatch.background, borderColor: swatch.tint }}>
    <i style={{ background: swatch.foreground }} /><i style={{ background: swatch.tint }} /><i style={{ background: swatch.accent }} /></span>;
}

/** Where a key moves the focus in a list of `count` options from option `index` (-1: none has it); null for any other key. */
export function optionAfterKey(key: string, index: number, count: number): number | null {
  if (count === 0) return null;
  return key === "ArrowDown" ? Math.min(count - 1, index + 1) : key === "ArrowUp" ? Math.max(0, index - 1)
    : key === "Home" ? 0 : key === "End" ? count - 1 : null;
}

// Up, Down, Home and End move through the schemes as they do in a native list.
function moveFocus(event: KeyboardEvent<HTMLUListElement>) {
  const items = Array.from(event.currentTarget.querySelectorAll<HTMLElement>("[role=option] > .bp6-menu-item"));
  const next = optionAfterKey(event.key, items.indexOf(document.activeElement as HTMLElement), items.length);
  if (next === null) return;
  event.preventDefault();
  items[next].focus();
}

/**
 * The color scheme as a dropdown: every scheme with its swatch for the theme on screen. Choosing one applies
 * it at once and leaves the list open, so that several can be tried in a row.
 */
export function ColorSchemeSelect({ id, value, variant, onChange }: {
  id?: string; value: string; variant: ColorVariant; onChange: (scheme: string) => void;
}) {
  const { t } = useShellLanguage();
  const current = colorSchemeOf(value);
  const menu = <Menu className="color-scheme-menu" role="listbox" aria-label={t("Color scheme")} onKeyDown={moveFocus}>
    {colorSchemes.map(scheme => <MenuItem key={scheme.id} roleStructure="listoption" selected={scheme.id === current.id} shouldDismissPopover={false}
      icon={<ColorSchemeSwatch palette={schemePalette(scheme, variant)} variant={variant} />} text={scheme.name} onClick={() => onChange(scheme.id)} />)}
  </Menu>;
  return <PopoverNext placement="bottom-end" matchTargetWidth content={menu}
    // The list opens on the scheme in use, not on its first one.
    onOpened={popover => popover.querySelector<HTMLElement>("[role=option][aria-selected=true] > .bp6-menu-item")?.focus()}>
    <Button id={id} className="color-scheme-select" alignText="start" aria-haspopup="listbox"
      icon={<ColorSchemeSwatch palette={schemePalette(current, variant)} variant={variant} />} endIcon={<AppIcon name="chevronDown" size={14} />}>{current.name}</Button>
  </PopoverNext>;
}
