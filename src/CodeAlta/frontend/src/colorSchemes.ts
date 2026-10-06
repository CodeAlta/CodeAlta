import { darkerPalette } from "./colorPalette";
import { blueprintPalette, generatedColorSchemes, type Palette, type PaletteColor } from "./colorSchemes.gen";

/** A color scheme of the window: the palettes of its dark and light themes. */
export type ColorScheme = Readonly<{ id: string; name: string; dark: Palette; light: Palette }>;

/** The theme a palette is for: the light one, the dark one, or the dark one with deeper surfaces. */
export type ColorVariant = "light" | "dark" | "darker";

export const defaultColorScheme = "blueprint";
export const colorSchemeStorageKey = "codealta.desktop.colorScheme.v1";

/** Blueprint's own palette first, then the generated schemes in the order of their generator. */
export const colorSchemes: readonly ColorScheme[] = [
  { id: defaultColorScheme, name: "Blueprint", dark: blueprintPalette, light: blueprintPalette },
  ...generatedColorSchemes,
];
export const colorSchemeIds: readonly string[] = colorSchemes.map(scheme => scheme.id);

/** The scheme with an id; Blueprint's own for an id that names none. */
export const colorSchemeOf = (id: string): ColorScheme => colorSchemes.find(scheme => scheme.id === id) ?? colorSchemes[0];

/** The palette a scheme shows in a theme. The darker theme has no palette of its own: it is made from the dark one. */
export function schemePalette(scheme: ColorScheme, variant: ColorVariant): Palette {
  return variant === "light" ? scheme.light : variant === "dark" ? scheme.dark : darkerPalette(scheme.dark);
}

/** The colors that stand for a palette in a picker. */
export type ColorSchemeSwatch = Readonly<{ background: string; tint: string; foreground: string; accent: string }>;
export function schemeSwatch(palette: Palette, variant: ColorVariant): ColorSchemeSwatch {
  return variant === "light"
    ? { background: palette["light-gray-5"], tint: palette["gray-4"], foreground: palette["dark-gray-1"], accent: palette["blue-3"] }
    : { background: palette["dark-gray-1"], tint: palette["gray-1"], foreground: palette["light-gray-5"], accent: palette["blue-4"] };
}

/** What the window shows: its theme, its scheme and the palette of that scheme for the theme. */
export type ShownAppearance = Readonly<{ theme: "dark" | "light"; scheme: string; palette: Palette }>;

/** The custom properties that show a palette: Blueprint's palette variables for the colors that differ from Blueprint's own. */
export function paletteVariables(palette: Palette): Readonly<Record<string, string>> {
  const variables: Record<string, string> = {};
  for (const name of Object.keys(blueprintPalette) as PaletteColor[])
    if (palette[name] !== blueprintPalette[name]) variables[`--bp-palette-${name}`] = palette[name];
  return variables;
}

function signature(text: string): string {
  let hash = 0x811c9dc5;
  for (let index = 0; index < text.length; index++) hash = Math.imul(hash ^ text.charCodeAt(index), 0x01000193);
  return (hash >>> 0).toString(16).padStart(8, "0");
}

/**
 * Shows a palette: sets the custom properties it needs on the root and removes those of the palette before.
 * The root's `data-palette` attribute then names what is set (absent for Blueprint's own palette), so that
 * what draws with the window's colors outside CSS (the code editor, diagrams) sees them change.
 */
export function applyPalette(root: Pick<HTMLElement, "style" | "dataset">, palette: Palette): void {
  const variables = paletteVariables(palette), style = root.style;
  // Only the palette's own properties: the root also carries those of the start-up screen.
  const stale: string[] = [];
  for (let index = 0; index < style.length; index++) {
    const property = style.item(index);
    if (property.startsWith("--bp-palette-") && !(property in variables)) stale.push(property);
  }
  for (const property of stale) style.removeProperty(property);
  const entries = Object.entries(variables);
  for (const [property, value] of entries) if (style.getPropertyValue(property) !== value) style.setProperty(property, value);
  if (entries.length === 0) delete root.dataset.palette;
  else root.dataset.palette = signature(entries.map(([property, value]) => `${property}:${value}`).join(";"));
}

/** Shows an appearance on the document root. `darkClass` is the class that turns Blueprint's components dark. */
export function showAppearance(root: Pick<HTMLElement, "style" | "dataset" | "classList">, darkClass: string, appearance: ShownAppearance): void {
  root.dataset.theme = appearance.theme;
  root.classList.toggle(darkClass, appearance.theme === "dark");
  if (appearance.scheme === defaultColorScheme) delete root.dataset.colorScheme;
  else root.dataset.colorScheme = appearance.scheme;
  applyPalette(root, appearance.palette);
}
