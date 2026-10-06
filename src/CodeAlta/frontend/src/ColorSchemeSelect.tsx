import { useMemo, type KeyboardEvent } from "react";
import { Button, Menu, MenuDivider, MenuItem, PopoverNext } from "@blueprintjs/core";
import { AppIcon } from "./AppIcon";
import { colorSchemes, customSchemeSelection, schemePalette, schemeSwatch, type ColorScheme, type ColorVariant, type CustomColorScheme } from "./colorSchemes";
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
 * The color scheme as a dropdown: every scheme with its swatch for the theme on screen, the built-in ones
 * first, then the user's. Choosing one applies it at once and leaves the list open, so that several can be
 * tried in a row.
 */
export function ColorSchemeSelect({ id, value, shown, custom, variant, disabled, onChange, onOpening }: {
  id?: string;
  /** The selection, and the scheme it stands for. */
  value: string; shown: ColorScheme | CustomColorScheme;
  /** The user's schemes. */
  custom: readonly CustomColorScheme[];
  variant: ColorVariant; disabled?: boolean; onChange: (selection: string) => void;
  /** Called when the list opens: the moment to read the user's schemes again. */
  onOpening?: () => void;
}) {
  const { t, locale } = useShellLanguage();
  // A palette for every scheme: made again when the schemes, the theme or the language change, not each time
  // the row renders (it does for every move of a color while a scheme is edited).
  const menu = useMemo(() => {
    const option = (selection: string, scheme: ColorScheme | CustomColorScheme) => <MenuItem key={selection} roleStructure="listoption" selected={selection === value}
      shouldDismissPopover={false} icon={<ColorSchemeSwatch palette={schemePalette(scheme, variant)} variant={variant} />} text={scheme.name} onClick={() => onChange(selection)} />;
    return <Menu className="color-scheme-menu" role="listbox" aria-label={t("Color scheme")} onKeyDown={moveFocus}>
      {colorSchemes.map(scheme => option(scheme.id, scheme))}
      {custom.length > 0 && <MenuDivider title={t("Your color schemes")} />}
      {custom.map(scheme => option(customSchemeSelection(scheme.id), scheme))}
    </Menu>;
  }, [value, custom, variant, onChange, locale]);
  const swatch = useMemo(() => <ColorSchemeSwatch palette={schemePalette(shown, variant)} variant={variant} />, [shown, variant]);
  return <PopoverNext className="color-scheme-target" placement="bottom-end" matchTargetWidth content={menu} disabled={disabled} onOpening={onOpening}
    // The list opens on the scheme in use, not on its first one.
    onOpened={popover => popover.querySelector<HTMLElement>("[role=option][aria-selected=true] > .bp6-menu-item")?.focus()}>
    <Button id={id} className="color-scheme-select" alignText="start" aria-haspopup="listbox" disabled={disabled}
      icon={swatch} endIcon={<AppIcon name="chevronDown" size={14} />}>{shown.name}</Button>
  </PopoverNext>;
}
